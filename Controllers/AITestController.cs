using Azure;
using Google.Protobuf.WellKnownTypes;
using LLama.Batched;
using LocalRagAPI.Models;
using LocalRagAPI.Services;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.VisualBasic;
using System.Data;
using System.Diagnostics.Metrics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Xml;
using static Google.Protobuf.WellKnownTypes.Field.Types;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;
using static UglyToad.PdfPig.Core.PdfSubpath;
using LocalRagAPI.Repositories;
using Microsoft.AspNetCore.Hosting;

namespace LocalRagAPI.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class AITestController : ControllerBase
    {
        private readonly ILLMService _llm;
        private readonly JinaEmbeddingService _embeddingService;
        private readonly ChatMemory _memory;
        private readonly JinaRerankerService _reranker;
        private readonly QdrantService _qdrant;
        private readonly PromptBuilderService _promptBuilder;
        private readonly Microsoft.Extensions.Logging.ILogger<AITestController> _logger;
        private readonly IDocumentRepository _documentRepository;
        private readonly IWebHostEnvironment _env;
        private readonly IChatSessionRepository _chatSessionRepository;
        private readonly IMessageRepository _messageRepository;
        private readonly IQueryLogRepository _queryLogRepository;
        private readonly FileHashService _fileHashService;

        public AITestController(
            ILLMService llm,
            JinaEmbeddingService embeddingService,
            ChatMemory memory,
            JinaRerankerService reranker,
            QdrantService qdrant,
            PromptBuilderService promptBuilder,
            Microsoft.Extensions.Logging.ILogger<AITestController> logger,
            IDocumentRepository documentRepository,
            IWebHostEnvironment env,
            IChatSessionRepository chatSessionRepository,
            IMessageRepository messageRepository,
            IQueryLogRepository queryLogRepository,
            FileHashService fileHashService)
        {
            _llm = llm;
            _embeddingService = embeddingService;
            _memory = memory;
            _reranker = reranker;
            _qdrant = qdrant;
            _promptBuilder = promptBuilder;
            _logger = logger;
            _documentRepository = documentRepository;
            _env = env;
            _chatSessionRepository = chatSessionRepository;
            _messageRepository = messageRepository;
            _queryLogRepository = queryLogRepository;
            _fileHashService = fileHashService;
        }

        private Guid GetCurrentUserId()
        {
            if (User?.Identity?.IsAuthenticated == true)
            {
                var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                          ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (Guid.TryParse(sub, out var parsed)) return parsed;
            }

            return Guid.Empty;
        }

        [HttpGet("ingest-status")]
        public IActionResult IngestStatus(string jobId)
        {
            if (string.IsNullOrEmpty(jobId)) return BadRequest(new { error = "jobId is required" });

            var jobStore = HttpContext.RequestServices.GetService(typeof(LocalRagAPI.Services.IngestionJobStore)) as LocalRagAPI.Services.IngestionJobStore;

            if (jobStore == null) return StatusCode(500);

            if (!jobStore.TryGet(jobId, out var status))
                return NotFound(new { error = "job not found" });

            return Ok(status);
        }

        [HttpGet]
        public async Task<string> Ask()
        {
            return await _llm.GenerateResponse(
                "Explain embeddings in simple terms."
            );
        }

        [HttpGet("embed")]
        public async Task<int> TestEmbedding()
        {
            var result = await _embeddingService.GenerateEmbeddings(
                new List<string> { "What is refund policy?" });

            return result[0].Length;
        }


        [HttpGet("ask-rag-stream")]
        public async Task AskRagStream(string question, string doc = null, string sessionId = null)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            if (string.IsNullOrWhiteSpace(question))
            {
                await Response.WriteAsync("data: Question cannot be empty.\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            var currentUserId = GetCurrentUserId();

            // determine or create session early so memory and persistence are scoped
            ChatSession sessionEarly = null;
            if (!string.IsNullOrEmpty(sessionId) && Guid.TryParse(sessionId, out var sid2))
            {
                sessionEarly = await _chatSessionRepository.GetByIdAsync(sid2);
            }

            if (sessionEarly == null)
            {
                sessionEarly = await _chatSessionRepository.CreateAsync(new ChatSession
                {
                    UserId = currentUserId,
                    Title = "Chat",
                    ExpiresAt = DateTime.UtcNow.AddDays(30)
                });
            }
            else if (currentUserId != Guid.Empty && sessionEarly.UserId != Guid.Empty && sessionEarly.UserId != currentUserId)
            {
                await Response.WriteAsync("data: Unauthorized session access.\n\n");
                await Response.Body.FlushAsync();
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            if (!await _qdrant.HasPointsAsync(doc, currentUserId == Guid.Empty ? null : currentUserId.ToString()))
            {
                var noDocsNotice = "No documents uploaded. Please upload a document first.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = noDocsNotice });
                }
                catch { }
                await Response.WriteAsync($"data: {noDocsNotice}\n\n");
                await Response.Body.FlushAsync();
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            var history = _memory.BuildConversationHistory(currentUserId, sessionEarly.Id);

            // optional rewrite
            string rewrittenQuestion = question;
            bool needsRewrite = NeedsRewrite(question);

            if (needsRewrite)
            {
                var contextualQuestion = $@"
                    Conversation History:
                    {history}

                    User Question:
                    {question}

                    Rewrite the question so it is clear for document search.
                    Return only the rewritten question.
                ";

                rewrittenQuestion = await _llm.GenerateResponse(contextualQuestion);
            }

            var queries = new List<string> { rewrittenQuestion };
            var embeddings = await _embeddingService.GenerateEmbeddings(queries);
            var userIdStr = currentUserId == Guid.Empty ? null : currentUserId.ToString();

            var vectorTasks = embeddings.Select(e => _qdrant.Search(e, doc, 50, userIdStr));
            var vectorResults = await Task.WhenAll(vectorTasks);
            var vectorItems = vectorResults.SelectMany(r => r).ToList();

            var keywordItems = await _qdrant.KeywordSearch(rewrittenQuestion, doc, 50, userIdStr);

            var candidateItems = vectorItems
                .Concat(keywordItems)
                .Where(i => !string.IsNullOrWhiteSpace(i.Content) && i.Content.Length > 30)
                .GroupBy(i => i.Content)
                .Select(g => g.First())
                .Take(60)
                .ToList();

            if (!candidateItems.Any())
            {
                var outOfScopeNotice = "This question is outside the scope of the uploaded documents.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = outOfScopeNotice });
                }
                catch { }
                await Response.WriteAsync($"data: {outOfScopeNotice}\n\n");
                await Response.Body.FlushAsync();
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            var candidateContents = candidateItems.Select(i => i.Content).ToList();
            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateContents);

            if (!rerankedChunks.Any())
            {
                var notFoundNotice = "I cannot find that information in the uploaded documents.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = notFoundNotice });
                }
                catch { }
                await Response.WriteAsync($"data: {notFoundNotice}\n\n");
                await Response.Body.FlushAsync();
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            // build context
            var contextBuilder = new StringBuilder();
            int sourceIndex = 1;
            foreach (var chunk in rerankedChunks.Take(4))
            {
                contextBuilder.AppendLine($"[Source {sourceIndex}]");
                contextBuilder.AppendLine(chunk);
                contextBuilder.AppendLine();
                sourceIndex++;
            }

            var combinedContext = contextBuilder.ToString();
            var prompt = _promptBuilder.BuildPrompt(combinedContext, history, question);

            // use the earlier session and persist user message (successful path)
            var session = sessionEarly;

            try
            {
                await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "user", Content = question });
            }
            catch { }

            // add to in-memory chat memory
            _memory.AddUserMessage(currentUserId, session.Id, question);

            // stream LLM tokens directly
            var builder = new StringBuilder();

            await foreach (var token in _llm.StreamResponse(prompt))
            {
                if (string.IsNullOrEmpty(token))
                    continue;

                // append to accumulated answer
                builder.Append(token);

                // forward token as SSE (raw, client buffers)
                await Response.WriteAsync($"data: {token}\n\n");
                await Response.Body.FlushAsync();
            }

            // send done
            // Before finishing, send a cleaned final payload so client can replace streamed partials
            string CleanFormatting(string text)
            {
                if (string.IsNullOrEmpty(text)) return string.Empty;

                // Normalize CRLF
                var t = text.Replace("\r\n", "\n");

                // Ensure headings are explicit
                t = System.Text.RegularExpressions.Regex.Replace(t, "(^|\n)(Summary)(?!\\n)", "$1### Summary\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                t = System.Text.RegularExpressions.Regex.Replace(t, "(^|\n)(Key Points)(?!\\n)", "$1### Key Points\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                t = System.Text.RegularExpressions.Regex.Replace(t, "(^|\n)(Detailed Explanation)(?!\\n)", "$1### Detailed Explanation\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                t = System.Text.RegularExpressions.Regex.Replace(t, "(^|\n)(Sources)(?!\\n)", "$1### Sources\n\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // Ensure a blank line before list items
                t = System.Text.RegularExpressions.Regex.Replace(t, "\n- ", "\n\n- ");

                // Collapse multiple blank lines
                t = System.Text.RegularExpressions.Regex.Replace(t, "\n{3,}", "\n\n");

                return t.Trim();
            }

            var finalClean = CleanFormatting(builder.ToString());

            // send final cleaned as a single SSE event prefixed with [FINAL]
            // write prefix line
            await Response.WriteAsync("data: [FINAL]\n");

            // write each line as data: to preserve newlines in event.data
            foreach (var line in finalClean.Split('\n'))
            {
                await Response.WriteAsync($"data: {line}\n");
            }

            await Response.WriteAsync("\n");
            await Response.Body.FlushAsync();

            // send done
            await Response.WriteAsync("data: [DONE]\n\n");
            await Response.Body.FlushAsync();

            // save memory with final assembled response
            var finalAnswer = builder.ToString();
            // persist assistant message and query log
            try
            {
                // attempt to map source document to a stored Document
                LocalRagAPI.Models.Document mappedDoc = null;
                var topSource = candidateItems.FirstOrDefault(ci => rerankedChunks.Take(4).Contains(ci.Content) && !string.IsNullOrEmpty(ci.Document));
                if (topSource != null)
                {
                    mappedDoc = await _documentRepository.GetByFileNameAsync(topSource.Document);
                }

                await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "assistant", Content = finalAnswer });

                await _queryLogRepository.CreateAsync(new QueryLog
                {
                    UserId = currentUserId,
                    DocumentId = mappedDoc?.Id,
                    Question = question,
                    Answer = finalAnswer
                });
            }
            catch { }
            // update in-memory conversation for this user/session
            try
            {
                _memory.AddAssistantMessage(currentUserId, session.Id, finalAnswer);
            }
            catch { }
        }



        [HttpDelete("document")]
        public async Task<IActionResult> DeleteDocument(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return BadRequest("Document name is required.");

            try
            {
                // support deleting by id or by filename
                LocalRagAPI.Models.Document doc = null;

                if (Guid.TryParse(name, out var id))
                {
                    doc = await _documentRepository.GetByIdAsync(id);
                }
                else
                {
                    doc = await _documentRepository.GetByFileNameAsync(name);
                }

                if (doc == null)
                    return NotFound(new { error = "Document not found" });

                // if auth enabled, ensure ownership
                if (User?.Identity?.IsAuthenticated == true)
                {
                    var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                              ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                    if (!Guid.TryParse(sub, out var uid) || uid != doc.UserId)
                    {
                        return Forbid();
                    }
                }

                // remove payloads from Qdrant scoped to this user
                try
                {
                    await _qdrant.DeleteByDocumentAndUserAsync(doc.FileName, doc.UserId.ToString());
                }
                catch
                {
                    // fallback to broad delete
                    await _qdrant.DeleteByDocumentAsync(doc.FileName);
                }

                // soft-delete record and remove file
                await _documentRepository.MarkDeletedAsync(doc.Id);

                try
                {
                    if (!string.IsNullOrEmpty(doc.FilePath) && System.IO.File.Exists(doc.FilePath))
                        System.IO.File.Delete(doc.FilePath);
                }
                catch { }

                return Ok(new { message = $"Document '{name}' deleted successfully." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting document {Document}", name);

                return StatusCode(500, "Failed to delete document.");
            }
        }


        // =========================
        // ASK QUESTION USING RAG
        // =========================

        [HttpGet("ask-rag")]
        public async Task<RagResponse> AskRag(string question, string doc = null, string sessionId = null)
        {
            var currentUserId = GetCurrentUserId();

            // determine or create session for this user
            ChatSession sessionEarly = null;
            if (!string.IsNullOrEmpty(sessionId) && Guid.TryParse(sessionId, out var sidE))
            {
                sessionEarly = await _chatSessionRepository.GetByIdAsync(sidE);
            }

            if (sessionEarly == null)
            {
                sessionEarly = await _chatSessionRepository.CreateAsync(new ChatSession
                {
                    UserId = currentUserId,
                    Title = "Chat",
                    ExpiresAt = DateTime.UtcNow.AddDays(30)
                });
            }
            else if (currentUserId != Guid.Empty && sessionEarly.UserId != Guid.Empty && sessionEarly.UserId != currentUserId)
            {
                return new RagResponse
                {
                    Answer = "Unauthorized session access.",
                    Sources = new List<string>()
                };
            }

            if (string.IsNullOrWhiteSpace(question))
            {
                return new RagResponse
                {
                    Answer = "Question cannot be empty.",
                    Sources = new List<string>()
                };
            }

            if (!await _qdrant.HasPointsAsync(doc, currentUserId == Guid.Empty ? null : currentUserId.ToString()))
            {
                var noDocsNotice = "No documents uploaded. Please upload a document first.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = noDocsNotice });
                }
                catch { }

                return new RagResponse
                {
                    Answer = noDocsNotice,
                    Sources = new List<string>()
                };
            }

            var lower = question.ToLower();

            // =============================
            // FAST GREETING SHORTCUT
            // =============================
            if (lower == "hello" || lower == "hi" || lower == "hey")
            {
                var quick = await _llm.GenerateResponse(question);

                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = quick });
                }
                catch { }

                return new RagResponse
                {
                    Answer = quick,
                    Sources = new List<string>()
                };
            }

            // =============================
            // STEP 1 — CONVERSATION HISTORY (scoped to user + session)
            // =============================

            var history = _memory.BuildConversationHistory(currentUserId, sessionEarly.Id);

            // =============================
            // STEP 2 — CONDITIONAL REWRITE
            // =============================

            string rewrittenQuestion = question;

            bool needsRewrite = NeedsRewrite(question);

            if (needsRewrite)
            {
                var contextualQuestion = $@"
                    Conversation History:
                    {history}

                    User Question:
                    {question}

                    Rewrite the question so it is clear for document search.
                    Return only the rewritten question.
                ";

                rewrittenQuestion = await _llm.GenerateResponse(contextualQuestion);
            }

            // =============================
            // STEP 3 — MULTI QUERY GENERATION
            // =============================

            var queries = new List<string> { rewrittenQuestion };

            // =============================
            // STEP 4 — EMBEDDINGS
            // =============================

            var embeddings = await _embeddingService.GenerateEmbeddings(queries);

            // =============================
            // STEP 5 — PARALLEL VECTOR SEARCH
            // =============================

            var userIdStr = currentUserId == Guid.Empty ? null : currentUserId.ToString();

            // VECTOR SEARCH — retrieve larger candidate pool
            var vectorTasks = embeddings.Select(e => _qdrant.Search(e, doc, 50, userIdStr));
            var vectorResults = await Task.WhenAll(vectorTasks);

            var vectorItems = vectorResults
                .SelectMany(r => r)
                .ToList();

            // KEYWORD MATCHING (LOCAL)
            var keywordItems = await _qdrant.KeywordSearch(rewrittenQuestion, doc, 50, userIdStr);

            // Merge vector + keyword results, filter tiny chunks and deduplicate by content
            var candidateItems = vectorItems
                .Concat(keywordItems)
                .Where(i => !string.IsNullOrWhiteSpace(i.Content) && i.Content.Length > 30)
                .GroupBy(i => i.Content)
                .Select(g => g.First())
                .Take(60)
                .ToList();

            if (!candidateItems.Any())
            {
                var outOfScopeNotice = "This question is outside the scope of the uploaded documents.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = outOfScopeNotice });
                }
                catch { }

                return new RagResponse
                {
                    Answer = outOfScopeNotice,
                    Sources = new List<string>()
                };
            }

            // =============================
            // STEP 6 — RERANK
            // =============================

            var candidateContents = candidateItems.Select(i => i.Content).ToList();

            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateContents);

            if (!rerankedChunks.Any())
            {
                var notFoundNotice = "I cannot find that information in the uploaded documents.";
                try
                {
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "user", Content = question });
                    await _messageRepository.AddAsync(new Message { SessionId = sessionEarly.Id, Role = "assistant", Content = notFoundNotice });
                }
                catch { }

                return new RagResponse
                {
                    Answer = notFoundNotice,
                    Sources = new List<string>()
                };
            }

            // =============================
            // STEP 7 — CONTEXT BUILDING
            // =============================

            // map content back to original search items for source metadata
            var itemByContent = candidateItems
                .GroupBy(i => i.Content)
                .ToDictionary(g => g.Key, g => g.First());

            var contextBuilder = new StringBuilder();

            int sourceIndex = 1;

            foreach (var chunk in rerankedChunks.Take(4))
            {
                contextBuilder.AppendLine($"[Source {sourceIndex}]");
                contextBuilder.AppendLine(chunk);
                contextBuilder.AppendLine();

                sourceIndex++;
            }

            var combinedContext = contextBuilder.ToString();

            // =============================
            // STEP 8 — FINAL PROMPT
            // =============================

            var prompt = _promptBuilder.BuildPrompt(combinedContext, history, question);

            var session = sessionEarly;

            try { await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "user", Content = question }); } catch { }
            try { _memory.AddUserMessage(currentUserId, session.Id, question); } catch { }

            var response = await _llm.GenerateResponse(prompt);

            // Force correct markdown formatting
            response = response
                .Replace("Summary", "### Summary\n")
                .Replace("Key Points", "\n### Key Points\n")
                .Replace("Detailed Explanation", "\n### Detailed Explanation\n")
                .Replace("Explanation", "\n### Explanation\n")
                .Replace("Sources", "\n### Sources\n");

            // =============================
            // STEP 9 — SAVE MEMORY AND PERSIST
            // =============================

            // persist assistant message and query log
            try
            {
                LocalRagAPI.Models.Document mappedDoc = null;
                // try to find a matching source document
                var topSource = candidateItems.FirstOrDefault(ci => rerankedChunks.Take(4).Contains(ci.Content) && !string.IsNullOrEmpty(ci.Document));
                if (topSource != null)
                {
                    mappedDoc = await _documentRepository.GetByFileNameAsync(topSource.Document);
                }

                await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "assistant", Content = response });

                await _queryLogRepository.CreateAsync(new QueryLog
                {
                    UserId = currentUserId,
                    DocumentId = mappedDoc?.Id,
                    Question = question,
                    Answer = response
                });
            }
            catch { }

            try { _memory.AddAssistantMessage(currentUserId, session.Id, response); } catch { }

            // =============================
            // STEP 10 — SOURCE DISPLAY
            // =============================

            var sources = new List<string>();

            // Use the reranked chunks and original search metadata for better source display
            if (rerankedChunks != null && rerankedChunks.Any())
            {
                foreach (var c in rerankedChunks.Take(3))
                {
                    if (itemByContent != null && itemByContent.TryGetValue(c, out var item) && !string.IsNullOrEmpty(item.Document))
                    {
                        sources.Add($"📄 {item.Document}");
                    }
                    else
                    {
                        sources.Add("📄 Uploaded Document");
                    }
                }
            }

            return new RagResponse
            {
                Answer = response,
                Sources = sources
            };
        }
        

        // =========================
        // GENERAL CHAT
        // =========================

        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Question))
                return BadRequest("Question cannot be empty.");

            var historyText = req.History != null
                ? string.Join("\n", req.History.TakeLast(10).Select(m => $"{m.Role}: {m.Content}"))
                : string.Empty;

            var prompt = $"""
You are a professional AI assistant helping users.

Guidelines:
- Be polite and concise
- Do not mention internal system limitations
- Continue the conversation naturally
- If the user refers to something earlier, infer the context

Conversation history:
{historyText}

User: {req.Question}

Assistant:
""";

            var response = await _llm.GenerateResponse(prompt);

            if (!string.IsNullOrEmpty(req.SessionId) && Guid.TryParse(req.SessionId, out var sid))
            {
                var currentUserId = GetCurrentUserId();
                var session = await _chatSessionRepository.GetByIdAsync(sid);
                if (session != null)
                {
                    if (currentUserId != Guid.Empty && session.UserId != Guid.Empty && session.UserId != currentUserId)
                    {
                        _logger.LogWarning("Unauthorized attempt by user {UserId} to write to session {SessionId} owned by {OwnerId}",
                            currentUserId, session.Id, session.UserId);
                        return Forbid();
                    }

                    try
                    {
                        await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "user", Content = req.Question });
                        await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "assistant", Content = response });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to persist chat message to session {SessionId}", session.Id);
                    }
                }
            }

            return Ok(new { answer = response });
        }


        // =========================
        // FILE UPLOAD
        // =========================
        [HttpPost("upload")]
        public async Task<ActionResult<string>> UploadFile(IFormFile file)
        {
            if (file == null || file.Length == 0)
                return "Invalid file.";

            Guid finalUserId = Guid.Empty;
            if (User?.Identity?.IsAuthenticated == true)
            {
                var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                          ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (Guid.TryParse(sub, out var parsed)) finalUserId = parsed;
            }

            var uploadService = HttpContext.RequestServices.GetService(typeof(LocalRagAPI.Services.DocumentUploadService)) as LocalRagAPI.Services.DocumentUploadService;
            if (uploadService == null)
                return StatusCode(500, "Upload service not configured.");

            using var stream = file.OpenReadStream();
            var result = await uploadService.UploadAsync(stream, file.FileName, finalUserId, file.Length);

            if (!result.IsSuccess)
            {
                if (result.Status == "Duplicate")
                    return Conflict(result.Message);
                else
                    return BadRequest(result.Message);
            }

            return Accepted(new { jobId = result.JobId, documentId = result.DocumentId });
        }

        private async Task ProcessDocument(string text, string documentName)
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();

            var sentences = text
                .Split(new[] { ".", "!", "?" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            int chunkSentenceSize = 6;
            int overlap = 2;
            int maxChunks = 300;

            var chunks = new List<string>();

            for (int i = 0; i < sentences.Count; i += (chunkSentenceSize - overlap))
            {
                var chunkSentences = sentences
                    .Skip(i)
                    .Take(chunkSentenceSize)
                    .ToList();

                if (!chunkSentences.Any())
                    break;

                var chunkText = string.Join(". ", chunkSentences) + ".";
                chunks.Add(chunkText);

                if (chunks.Count >= maxChunks)
                    break;
            }

            // Batch embedding requests to avoid huge payloads and improve throughput
            // Increased batch size to reduce total number of embedding requests.
            int batchSize = 256;

            // Build list of batches
            var batches = new List<List<string>>();
            for (int i = 0; i < chunks.Count; i += batchSize)
            {
                batches.Add(chunks.Skip(i).Take(batchSize).ToList());
            }

            int maxConcurrency = 3; // controlled concurrency for embedding requests
            var semaphore = new System.Threading.SemaphoreSlim(maxConcurrency);
            var tasks = new List<Task>();

            for (int b = 0; b < batches.Count; b++)
            {
                var batchIndex = b;
                var batch = batches[b];

                var work = Task.Run(async () =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        var swBatch = System.Diagnostics.Stopwatch.StartNew();
                        var embBatch = await _embeddingService.GenerateEmbeddings(batch);
                        swBatch.Stop();
                        _logger?.LogInformation("Embedding batch {BatchIndex}: generated {Count} embeddings in {Elapsed}ms", batchIndex, embBatch.Count, swBatch.ElapsedMilliseconds);

                        var items = new List<(string content, string document, float[] vector, string userId, string documentId)>();

                        // ⚠️ IMPORTANT: since this method doesn't have userId/documentId,
                        // we pass null (or you can pass actual values if available)

                        string userId = null;
                        string documentId = null;

                        for (int j = 0; j < embBatch.Count; j++)
                        {
                            items.Add((
                                content: batch[j],
                                document: documentName,
                                vector: embBatch[j],
                                userId: userId,
                                documentId: documentId
                            ));
                        }

                        await _qdrant.BatchUpsertAsync(items);
                        //var points = new List<Qdrant.Client.Grpc.PointStruct>();
                        //for (int j = 0; j < embBatch.Count; j++)
                        //{
                        //    var point = new Qdrant.Client.Grpc.PointStruct
                        //    {
                        //        Id = new Qdrant.Client.Grpc.PointId { Uuid = Guid.NewGuid().ToString() },
                        //        Vectors = embBatch[j]
                        //    };

                        //    point.Payload.Add("document", documentName);
                        //    point.Payload.Add("content", batch[j]);
                        //    points.Add(point);
                        //}

                        //var swUpsert = System.Diagnostics.Stopwatch.StartNew();
                        //await _qdrant.BatchUpsertAsync(points);
                        //swUpsert.Stop();
                        //_logger?.LogInformation("Upsert batch {BatchIndex}: upserted {Count} points in {Elapsed}ms", batchIndex, points.Count, swUpsert.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing embedding batch {BatchIndex}", batchIndex);
                        throw;
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

                tasks.Add(work);
            }

            await Task.WhenAll(tasks);

            swTotal.Stop();
            _logger?.LogInformation("Processed document {DocumentName}: totalChunks={Chunks} totalElapsed={Elapsed}ms", documentName, chunks.Count, swTotal.ElapsedMilliseconds);
        }

        private bool NeedsRewrite(string question)
        {
            var q = question.ToLower();

            if (question.Split(" ").Length <= 3)
                return true;

            if (q.Contains("it") || q.Contains("they") || q.Contains("this") || q.Contains("that"))
                return true;

            return false;
        }
    }
}
