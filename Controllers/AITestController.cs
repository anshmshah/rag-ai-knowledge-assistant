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
            IQueryLogRepository queryLogRepository)
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

            if (!await _qdrant.HasPointsAsync(doc, currentUserId == Guid.Empty ? null : currentUserId.ToString()))
            {
                await Response.WriteAsync("data: No documents uploaded. Please upload a document first.\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            // determine or create session early so memory is scoped
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
                await Response.WriteAsync("data: This question is outside the scope of the uploaded documents.\n\n");
                await Response.Body.FlushAsync();
                await Response.WriteAsync("data: [DONE]\n\n");
                await Response.Body.FlushAsync();
                return;
            }

            var candidateContents = candidateItems.Select(i => i.Content).ToList();
            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateContents);

            if (!rerankedChunks.Any())
            {
                await Response.WriteAsync("data: I cannot find that information in the uploaded documents.\n\n");
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

            // use the earlier session and persist user message
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

            if (!await _qdrant.HasPointsAsync(doc, currentUserId == Guid.Empty ? null : currentUserId.ToString()))
            {
                return new RagResponse
                {
                    Answer = "No documents uploaded. Please upload a document first.",
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

            var lower = question.ToLower();

            // =============================
            // FAST GREETING SHORTCUT
            // =============================
            if (lower == "hello" || lower == "hi" || lower == "hey")
            {
                var quick = await _llm.GenerateResponse(question);

                return new RagResponse
                {
                    Answer = quick,
                    Sources = new List<string>()
                };
            }

            // =============================
            // STEP 1 — CONVERSATION HISTORY (scoped to user + session)
            // =============================

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

            var history = _memory.BuildConversationHistory(currentUserId, sessionEarly.Id);

            // =============================
            // STEP 2 — CONDITIONAL REWRITE
            // =============================

            string rewrittenQuestion = question;

            bool needsRewrite = NeedsRewrite(question);

            //old code works faster 11.03.26 12:19

            //bool needsRewrite =
            //    question.Split(" ").Length <= 3 ||
            //    lower.Contains("it") ||
            //    lower.Contains("they") ||
            //    lower.Contains("this") ||
            //    lower.Contains("that");

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
            var keywords = rewrittenQuestion
                .ToLower()
                .Split(" ", StringSplitOptions.RemoveEmptyEntries);


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
                return new RagResponse
                {
                    Answer = "This question is outside the scope of the uploaded documents.",
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
                return new RagResponse
                {
                    Answer = "I cannot find that information in the uploaded documents.",
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

            

            // session handling and persistence
            ChatSession session = null;
            if (!string.IsNullOrEmpty(sessionId) && Guid.TryParse(sessionId, out var sid))
            {
                session = await _chatSessionRepository.GetByIdAsync(sid);
            }

            if (session == null)
            {
                session = await _chatSessionRepository.CreateAsync(new ChatSession
                {
                    UserId = currentUserId,
                    Title = "Chat",
                    ExpiresAt = DateTime.UtcNow.AddDays(30)
                });
            }

            try { await _messageRepository.AddAsync(new Message { SessionId = session.Id, Role = "user", Content = question }); } catch { }
            try { _memory.AddUserMessage(currentUserId, session.Id, question); } catch { }


            //var prompt = $@"
            //    You are an AI assistant for answering questions from company documents.

            //    Rules:
            //    - Answer ONLY using the provided context.
            //    - If the answer is not present say:
            //    'I cannot find that information in the uploaded documents.'
            //    - Always cite the source number like [Source 1].

            //    Context:
            //    {combinedContext}

            //    Conversation History:
            //    {history}

            //    Question:
            //    {question}

            //    Provide a clear answer and include source citations.
            //";

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
            var historyText = string.Join("\n",
                req.History.TakeLast(10).Select(m => $"{m.Role}: {m.Content}")
            );

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

            if (file.Length > 5 * 1024 * 1024)
                return "File too large. Max 5MB allowed.";

            string text;

            try
            {
                if (file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    text = await reader.ReadToEndAsync();
                }
                else if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                {
                    using var stream = file.OpenReadStream();
                    using var document = UglyToad.PdfPig.PdfDocument.Open(stream);

                    var sb = new StringBuilder();

                    foreach (var page in document.GetPages())
                        sb.AppendLine(page.Text);

                    text = sb.ToString();
                }
                else
                {
                    return "Unsupported file type. Only .txt and .pdf allowed.";
                }

                if (string.IsNullOrWhiteSpace(text))
                    return "File contains no readable text.";
            }
            catch (Exception ex)
            {
                return $"Error reading file: {ex.Message}";
            }

            // Create ingestion job metadata
            var jobId = Guid.NewGuid().ToString();

            var job = new LocalRagAPI.Models.IngestionJobStatus
            {
                JobId = jobId,
                State = LocalRagAPI.Models.IngestionJobState.Queued,
                CreatedAt = DateTime.UtcNow,
                CompletedBatches = 0,
                TotalBatches = 0
            };

            var jobStore = HttpContext.RequestServices.GetService(typeof(LocalRagAPI.Services.IngestionJobStore)) as LocalRagAPI.Services.IngestionJobStore;
            var queue = HttpContext.RequestServices.GetService(typeof(LocalRagAPI.Services.DocumentIngestionQueue)) as LocalRagAPI.Services.DocumentIngestionQueue;

            jobStore?.AddJob(job);

            // Determine user ownership. If authenticated use claim, otherwise use Guid.Empty as "local" user
            Guid finalUserId = Guid.Empty;
            if (User?.Identity?.IsAuthenticated == true)
            {
                var sub = User.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                          ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;

                if (Guid.TryParse(sub, out var parsed)) finalUserId = parsed;
            }

            // Persist file on disk under /uploads/{userId}/{documentId}.{ext}
            // Prevent creating duplicate active document with same filename for the same user
            var existing = await _documentRepository.GetByFileNameAsync(file.FileName);
            if (existing != null && existing.UserId == finalUserId)
            {
                return Conflict($"A non-deleted document with name '{file.FileName}' already exists.");
            }

            var docEntity = new LocalRagAPI.Models.Document
            {
                UserId = finalUserId,
                FileName = file.FileName
            };

            var uploadsRoot = Path.Combine(_env.ContentRootPath, "uploads");
            var userFolder = Path.Combine(uploadsRoot, finalUserId.ToString());
            Directory.CreateDirectory(userFolder);

            var ext = Path.GetExtension(file.FileName) ?? string.Empty;
            var diskFileName = docEntity.Id.ToString() + ext;
            var diskPath = Path.Combine(userFolder, diskFileName);

            try
            {
                await using (var fs = new FileStream(diskPath, FileMode.Create))
                {
                    await file.CopyToAsync(fs);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to save uploaded file");
                return StatusCode(500, "Failed to save uploaded file");
            }

            docEntity.FilePath = diskPath;
            await _documentRepository.CreateAsync(docEntity);

            var request = new LocalRagAPI.Models.DocumentIngestionRequest
            {
                JobId = jobId,
                DocumentName = file.FileName,
                Text = text,
                FileName = file.FileName,
                DocumentId = docEntity.Id,
                UserId = docEntity.UserId
            };

            // try enqueue with short timeout
            var enqueued = await queue.EnqueueAsync(request, TimeSpan.FromSeconds(5));

            if (!enqueued)
            {
                jobStore?.MarkFailed(jobId, "Queue is full");
                return StatusCode(429, "Server busy, try again later.");
            }

            // return 202 with job id and document id
            return Accepted(new { jobId, documentId = docEntity.Id });
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

                        var points = new List<Qdrant.Client.Grpc.PointStruct>();
                        for (int j = 0; j < embBatch.Count; j++)
                        {
                            var point = new Qdrant.Client.Grpc.PointStruct
                            {
                                Id = new Qdrant.Client.Grpc.PointId { Uuid = Guid.NewGuid().ToString() },
                                Vectors = embBatch[j]
                            };

                            point.Payload.Add("document", documentName);
                            point.Payload.Add("content", batch[j]);
                            points.Add(point);
                        }

                        var swUpsert = System.Diagnostics.Stopwatch.StartNew();
                        await _qdrant.BatchUpsertAsync(points);
                        swUpsert.Stop();
                        _logger?.LogInformation("Upsert batch {BatchIndex}: upserted {Count} points in {Elapsed}ms", batchIndex, points.Count, swUpsert.ElapsedMilliseconds);
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
