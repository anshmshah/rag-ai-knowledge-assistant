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

        public AITestController(
            ILLMService llm,
            JinaEmbeddingService embeddingService,
            ChatMemory memory,
            JinaRerankerService reranker,
            QdrantService qdrant)
        {
            _llm = llm;
            _embeddingService = embeddingService;
            _memory = memory;
            _reranker = reranker;
            _qdrant = qdrant;
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
        public async Task AskRagStream(string question, string doc = null)
        {
            Response.ContentType = "text/event-stream";
            Response.Headers.Add("Cache-Control", "no-cache");
            Response.Headers.Add("Connection", "keep-alive");

            if (string.IsNullOrWhiteSpace(question))
            {
                await Response.WriteAsync("data: Question cannot be empty.\n\n");
                return;
            }

            // Step 1 — Build RAG answer normally
            var ragResponse = await AskRag(question, doc);

            // Step 2 — Stream tokens instead of full text
            var words = ragResponse.Answer.Split(" ");

            foreach (var word in words)
            {
                await Response.WriteAsync($"data: {word} \n\n");
                await Response.Body.FlushAsync();
                await Task.Delay(10);
            }

            await Response.WriteAsync("data: [DONE]\n\n");
            await Response.Body.FlushAsync();
        }
        // working code
        //[HttpGet("ask-rag-stream")]
        //public async Task AskRagStream(string question, string doc = null)
        //{
        //    Response.ContentType = "text/event-stream";
        //    Response.Headers.Add("Cache-Control", "no-cache");
        //    Response.Headers.Add("Connection", "keep-alive");

        //    var ragResponse = await AskRag(question, doc);

        //    foreach (var word in ragResponse.Answer.Split("\n"))
        //    {
        //        await Response.WriteAsync($"data: {word} \n\n");
        //        await Response.Body.FlushAsync();
        //        await Task.Delay(25);
        //    }

        //    await Response.WriteAsync("data: [DONE]\n\n");
        //    await Response.Body.FlushAsync();
        //}




        // =========================
        // ASK QUESTION USING RAG
        // =========================

        [HttpGet("ask-rag")]
        public async Task<RagResponse> AskRag(string question,string doc = null)
        {
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
            // STEP 1 — CONVERSATION HISTORY
            // =============================

            var history = _memory.BuildConversationHistory();

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

            //var multiQueryPrompt = $@"
            //    Generate 3 search queries for retrieving relevant documents.

            //    Question:
            //    {rewrittenQuestion}

            //    Return one query per line.
            //";

            //var multiQueriesText = await _llm.GenerateResponse(multiQueryPrompt);

            //var queries = multiQueriesText
            //    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            //    .Select(q => q.Trim())
            //    .ToList();

            //queries.Add(rewrittenQuestion);

            var queries = new List<string> { rewrittenQuestion };

            // =============================
            // STEP 4 — EMBEDDINGS
            // =============================

            var embeddings = await _embeddingService.GenerateEmbeddings(queries);

            // =============================
            // STEP 5 — PARALLEL VECTOR SEARCH
            // =============================


            // VECTOR SEARCH
            var vectorTasks = embeddings.Select(e => _qdrant.Search(e, doc));
            var vectorResults = await Task.WhenAll(vectorTasks);

            var vectorChunks = vectorResults
                .SelectMany(r => r)
                .Distinct()
                .ToList();

            // KEYWORD MATCHING (LOCAL)
            var keywords = rewrittenQuestion
                .ToLower()
                .Split(" ", StringSplitOptions.RemoveEmptyEntries);


            var keywordChunks = await _qdrant.KeywordSearch(rewrittenQuestion, doc);
            //working and also fast chage on 11.03.26 12;15

            //var keywordChunks = vectorChunks
            //    .Where(chunk =>
            //        keywords.Any(k =>
            //            chunk.ToLower().Contains(rewrittenQuestion.ToLower())))
            //    .ToList();

            // MERGE VECTOR + KEYWORD RESULTS

            var candidateChunks = vectorChunks
    .Concat(keywordChunks)
    .Where(c => c.Length > 50)
    .Distinct()
    .Take(12)
    .ToList();
            //old code working 11.03.26 12:22

            //var candidateChunks = vectorChunks
            //    .Concat(keywordChunks)
            //    .Distinct()
            //    .Take(12)
            //    .ToList();

            //working fast before implementation of hybrid search

            //var searchTasks = embeddings
            //    .Select(e => _qdrant.Search(e,doc));

            //var searchResults = await Task.WhenAll(searchTasks);

            //var candidateChunks = searchResults
            //    .SelectMany(r => r)
            //    .Distinct()
            //    .ToList();

            if (!candidateChunks.Any())
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

            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateChunks);

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

            var prompt = $@"You are an AI assistant that answers questions using the provided documents.

Instructions:
- Use ONLY the provided context to answer the question.
- If the answer is not found in the context, respond exactly with:
'I cannot find that information in the uploaded documents.'
- Do not invent information.

IMPORTANT:
Always leave a blank line after headings.
Never place headings and text on the same line.

When an answer exists, format the response using Markdown.

Structure:

### Summary
Short explanation of the answer.

### Key Points
- Important point
- Important point
- Important point

### Detailed Explanation
Provide a detailed explanation based only on the context.

### Sources
List the sources used such as:
- [Source 1]
- [Source 2]

Formatting rules:
- Always leave a blank line after headings
- Use bullet points with '-'
- Never place headings inline with text

Context:
{combinedContext}

Conversation History:
{history}

Question:
{question}";

//            var prompt = $@"
//You are an AI assistant for answering questions from company documents.

//Answer ONLY using the provided context.

//Use short paragraphs and bullet points when appropriate.
//Avoid long walls of text.

//Format your answer EXACTLY like this:

//### Summary
//Provide a short 2-3 sentence summary.

//### Key Points
//- Bullet point
//- Bullet point
//- Bullet point

//### Detailed Explanation
//Explain clearly using paragraphs.

//### Sources
//Cite sources like [Source 1].

//Rules:
//- Use bullet points when possible
//- Do NOT generate information not present in context
//- If the answer is not in the context say:

//'I cannot find that information in the uploaded documents.'

//Context:
//{combinedContext}

//Conversation History:
//{history}

//Question:
//{question}
//";


            //working code 09/03/2026

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
            // STEP 9 — SAVE MEMORY
            // =============================

            _memory.AddUserMessage(question);
            _memory.AddAssistantMessage(response);

            // =============================
            // STEP 10 — SOURCE DISPLAY
            // =============================

            var sources = new List<string>();

            if (!response.Contains("I cannot find", StringComparison.OrdinalIgnoreCase))
            {
                sources = Enumerable
                    .Repeat("📄 Uploaded Document", 3)
                    .ToList();
            }

            return new RagResponse
            {
                Answer = response,
                Sources = sources
            };
        }
        //old working code slow 

        //        [HttpGet("ask-rag")]
        //        public async Task<RagResponse> AskRag(string question)
        //        {
        //            if (string.IsNullOrWhiteSpace(question))
        //            {
        //                return new RagResponse
        //                {
        //                    Answer = "Question cannot be empty.",
        //                    Sources = new List<string>()
        //                };
        //            }

        //            // =============================
        //            // STEP 1 — FOLLOW-UP CONTEXT
        //            // =============================

        //            var history = _memory.BuildConversationHistory();

        //            var contextualQuestion = $@"
        //Conversation History:
        //{history}

        //User Question:
        //{question}

        //Rewrite the question so it is clear for document search.
        //Return only the rewritten question.
        //";

        //            var rewrittenQuestion = await _llm.GenerateResponse(contextualQuestion);

        //            var lower = question.ToLower();

        //            if (lower == "hello" || lower == "hi" || lower == "hey")
        //            {
        //                var quick = await _llm.GenerateResponse(question);

        //                return new RagResponse
        //                {
        //                    Answer = quick,
        //                    Sources = new List<string>()
        //                };
        //            }

        //            // =============================
        //            // STEP 2 — EMBEDDING SEARCH
        //            // =============================

        //            var questionEmbedding = (await _embeddingService
        //                .GenerateEmbeddings(new List<string> { rewrittenQuestion }))[0];

        //            // =============================
        //            // STEP 3 — VECTOR SEARCH (QDRANT)
        //            // =============================

        //            var candidateChunks = await _qdrant.Search(questionEmbedding);

        //            if (!candidateChunks.Any())
        //            {
        //                return new RagResponse
        //                {
        //                    Answer = "This question is outside the scope of the uploaded documents.",
        //                    Sources = new List<string>()
        //                };
        //            }

        //            // =============================
        //            // STEP 4 — RERANK
        //            // =============================

        //            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateChunks);

        //            // =============================
        //            // STEP 5 — SOURCE CITATION
        //            // =============================

        //            var contextBuilder = new StringBuilder();
        //            int sourceIndex = 1;

        //            foreach (var chunk in rerankedChunks)
        //            {
        //                contextBuilder.AppendLine($"[Source {sourceIndex}]");
        //                contextBuilder.AppendLine(chunk);
        //                contextBuilder.AppendLine();

        //                sourceIndex++;
        //            }

        //            var combinedContext = contextBuilder.ToString();

        //            // =============================
        //            // STEP 6 — FINAL PROMPT
        //            // =============================

        //            var prompt = $@"
        //You are an AI assistant for answering questions from company documents.

        //Rules:
        //- Answer ONLY using the provided context.
        //- If the answer is not present say:
        //'I cannot find that information in the uploaded documents.'
        //- Always cite the source number like [Source 1].

        //Context:
        //{combinedContext}

        //Conversation History:
        //{history}

        //Question:
        //{question}

        //Provide a clear answer and include source citations.
        //";

        //            var response = await _llm.GenerateResponse(prompt);

        //            // =============================
        //            // STEP 7 — SAVE MEMORY
        //            // =============================

        //            _memory.AddUserMessage(question);
        //            _memory.AddAssistantMessage(response);

        //            var sources = new List<string>();

        //            // Only show sources if the AI actually found an answer
        //            if (!response.Contains("I cannot find", StringComparison.OrdinalIgnoreCase))
        //            {
        //                sources = rerankedChunks
        //                    .Take(3)
        //                    .Select(_ => $"📄 {Request.Query["document"]}")
        //                    .ToList();
        //            }

        //            return new RagResponse
        //            {
        //                Answer = response,
        //                Sources = sources
        //            };
        //        }

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


        //working but dont have chat memory


        //[HttpGet("chat")]
        //public async Task<RagResponse> Chat(string question)
        //{
        //    if (string.IsNullOrWhiteSpace(question))
        //    {
        //        return new RagResponse
        //        {
        //            Answer = "Please ask a question.",
        //            Sources = new List<string>()
        //        };
        //    }

        //    var answer = await _llm.GenerateResponse(question);

        //    return new RagResponse
        //    {
        //        Answer = answer,
        //        Sources = new List<string>()
        //    };
        //}

        //.....old working code but slow response.......

        //[HttpGet("chat")]
        //public async Task<RagResponse> Chat(string question, string mode = "rag")
        //{
        //    if (mode == "chat")
        //    {
        //        var answer = await _llm.GenerateResponse(question);

        //        return new RagResponse
        //        {
        //            Answer = answer,
        //            Sources = new List<string>()
        //        };
        //    }

        //    return await AskRag(question);
        //}

        // =========================
        // FILE UPLOAD
        // =========================
        [HttpPost("upload")]
        public async Task<string> UploadFile(IFormFile file)
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

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await ProcessDocument(text, file.FileName);

            stopwatch.Stop();

            return $"File processed successfully in {stopwatch.ElapsedMilliseconds} ms";
        }

        // =========================
        // DOCUMENT PROCESSING
        // =========================
        private async Task ProcessDocument(string text, string documentName)
        {

            var words = text.Split(" ", StringSplitOptions.RemoveEmptyEntries);

            int chunkSize = 250;
            int overlap = 50;

            var chunks = new List<string>();

            for (int i = 0; i < words.Length; i += chunkSize - overlap)
            {
                var chunkWords = words.Skip(i).Take(chunkSize);
                var chunkText = string.Join(" ", chunkWords);

                chunks.Add(chunkText);
            }

            //by sentence

            //var sentences = text
            //    .Split(new[] { ".", "!", "?" }, StringSplitOptions.RemoveEmptyEntries)
            //    .Select(s => s.Trim())
            //    .Where(s => !string.IsNullOrWhiteSpace(s))
            //    .ToList();

            //int chunkSentenceSize = 8;
            //int overlap = 2;
            //int maxChunks = 200;

            //var chunks = new List<string>();

            //for (int i = 0; i < sentences.Count; i += (chunkSentenceSize - overlap))
            //{
            //    if (chunks.Count >= maxChunks)
            //        break;

            //    var chunkSentences = sentences
            //        .Skip(i)
            //        .Take(chunkSentenceSize)
            //        .ToList();

            //    if (!chunkSentences.Any())
            //        break;

            //    var chunkText = string.Join(". ", chunkSentences) + ".";
            //    chunks.Add(chunkText);
            //}

            var embeddings = await _embeddingService.GenerateEmbeddings(chunks);

            for (int i = 0; i < chunks.Count; i++)
            {
                await _qdrant.InsertChunk(
                    documentName,
                    chunks[i],
                    embeddings[i]
                );
            }
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

//finaly working version 

//using LocalRagAPI.Models;
//using LocalRagAPI.Services;
//using Microsoft.AspNetCore.Mvc;
//using System.Text;

//namespace LocalRagAPI.Controllers
//{
//    [Route("api/[controller]")]
//    [ApiController]
//    public class AITestController : ControllerBase
//    {
//        private readonly ILLMService _llm;
//        private readonly JinaEmbeddingService _embeddingService;
//        private readonly ChatMemory _memory;
//        private readonly JinaRerankerService _reranker;
//        private readonly QdrantService _qdrant;

//        public AITestController(
//            ILLMService llm,
//            JinaEmbeddingService embeddingService,
//            ChatMemory memory,
//            JinaRerankerService reranker,
//            QdrantService qdrant)
//        {
//            _llm = llm;
//            _embeddingService = embeddingService;
//            _memory = memory;
//            _reranker = reranker;
//            _qdrant = qdrant;
//        }

//        [HttpGet]
//        public async Task<string> Ask()
//        {
//            return await _llm.GenerateResponse(
//                "Explain embeddings in simple terms."
//            );
//        }

//        [HttpGet("embed")]
//        public async Task<int> TestEmbedding()
//        {
//            var result = await _embeddingService.GenerateEmbeddings(
//                new List<string> { "What is refund policy?" });

//            return result[0].Length;
//        }

//        // =========================
//        // ASK QUESTION USING RAG
//        // =========================
//        [HttpGet("ask-rag")]
//        public async Task<RagResponse> AskRag(string question)
//        {
//            if (string.IsNullOrWhiteSpace(question))
//                return new RagResponse
//                {
//                    Answer = Response,
//                    Sources = rerankerdChunks.Take(3),
//                    ToList()
//                }; "Question cannot be empty.";

//            // =============================
//            // STEP 1 — FOLLOW-UP CONTEXT
//            // =============================

//            var history = _memory.BuildConversationHistory();

//            var contextualQuestion = $@"
//                Conversation History:
//                {history}

//                User Question:
//                {question}

//                Rewrite the question so it is clear for document search.
//                Return only the rewritten question.
//            ";

//            var rewrittenQuestion = await _llm.GenerateResponse(contextualQuestion);

//            // =============================
//            // STEP 2 — EMBEDDING SEARCH
//            // =============================

//            var questionEmbedding = (await _embeddingService
//                .GenerateEmbeddings(new List<string> { rewrittenQuestion }))[0];

//            // =============================
//            // STEP 3 — VECTOR SEARCH (QDRANT)
//            // =============================

//            var candidateChunks = await _qdrant.Search(questionEmbedding);

//            if (!candidateChunks.Any())
//                return "This question is outside the scope of the uploaded documents.";

//            // =============================
//            // STEP 4 — RERANK
//            // =============================

//            var rerankedChunks = await _reranker.Rerank(rewrittenQuestion, candidateChunks);

//            // =============================
//            // STEP 5 — SOURCE CITATION
//            // =============================

//            var contextBuilder = new StringBuilder();

//            int sourceIndex = 1;

//            foreach (var chunk in rerankedChunks)
//            {
//                contextBuilder.AppendLine($"[Source {sourceIndex}]");
//                contextBuilder.AppendLine(chunk);
//                contextBuilder.AppendLine();

//                sourceIndex++;
//            }

//            var combinedContext = contextBuilder.ToString();

//            // =============================
//            // STEP 6 — FINAL PROMPT
//            // =============================

//            var prompt = $@"
//You are an AI assistant for answering questions from company documents.

//Rules:
//- Answer ONLY using the provided context.
//- If the answer is not present say:
//'I cannot find that information in the uploaded documents.'
//- Always cite the source number like [Source 1].

//Context:
//{combinedContext}

//Conversation History:
//{history}

//Question:
//{question}

//Provide a clear answer and include source citations.
//";

//            var response = await _llm.GenerateResponse(prompt);

//            // =============================
//            // STEP 7 — SAVE MEMORY
//            // =============================

//            _memory.AddUserMessage(question);
//            _memory.AddAssistantMessage(response);

//            return response;
//        }

//        //chat 

//        [HttpGet("chat")]
//        public async Task<string> Chat(string question, string mode = "rag")
//        {
//            if (mode == "chat")
//            {
//                return await _llm.GenerateResponse(question);
//            }

//            return await AskRag(question);
//        }

//        // =========================
//        // FILE UPLOAD
//        // =========================
//        [HttpPost("upload")]
//        public async Task<string> UploadFile(IFormFile file)
//        {
//            if (file == null || file.Length == 0)
//                return "Invalid file.";

//            if (file.Length > 5 * 1024 * 1024)
//                return "File too large. Max 5MB allowed.";

//            string text;

//            try
//            {
//                if (file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
//                {
//                    using var reader = new StreamReader(file.OpenReadStream());
//                    text = await reader.ReadToEndAsync();
//                }
//                else if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
//                {
//                    using var stream = file.OpenReadStream();
//                    using var document = UglyToad.PdfPig.PdfDocument.Open(stream);

//                    var sb = new StringBuilder();

//                    foreach (var page in document.GetPages())
//                        sb.AppendLine(page.Text);

//                    text = sb.ToString();
//                }
//                else
//                {
//                    return "Unsupported file type. Only .txt and .pdf allowed.";
//                }

//                if (string.IsNullOrWhiteSpace(text))
//                    return "File contains no readable text.";
//            }
//            catch (Exception ex)
//            {
//                return $"Error reading file: {ex.Message}";
//            }

//            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//            await ProcessDocument(text, file.FileName);

//            stopwatch.Stop();

//            return $"File processed successfully in {stopwatch.ElapsedMilliseconds} ms";
//        }

//        // =========================
//        // DOCUMENT PROCESSING
//        // =========================
//        private async Task ProcessDocument(string text, string documentName)
//        {
//            var sentences = text
//                .Split(new[] { ".", "!", "?" },
//                StringSplitOptions.RemoveEmptyEntries)
//                .Select(s => s.Trim())
//                .Where(s => !string.IsNullOrWhiteSpace(s))
//                .ToList();

//            int chunkSentenceSize = 8;
//            int overlap = 2;
//            int maxChunks = 200;

//            var chunks = new List<string>();

//            for (int i = 0; i < sentences.Count; i += (chunkSentenceSize - overlap))
//            {
//                if (chunks.Count >= maxChunks)
//                    break;

//                var chunkSentences = sentences
//                    .Skip(i)
//                    .Take(chunkSentenceSize)
//                    .ToList();

//                if (!chunkSentences.Any())
//                    break;

//                var chunkText = string.Join(". ", chunkSentences) + ".";
//                chunks.Add(chunkText);
//            }

//            var embeddings = await _embeddingService.GenerateEmbeddings(chunks);

//            for (int i = 0; i < chunks.Count; i++)
//            {
//                await _qdrant.InsertChunk(
//                    documentName,
//                    chunks[i],
//                    embeddings[i]
//                );
//            }
//        }
//    }
//}


//working version of single file upload

//using LocalRagAPI.Models;
//using LocalRagAPI.Services;
//using Microsoft.AspNetCore.Mvc;
//using System.Text;

//namespace LocalRagAPI.Controllers
//{
//    [Route("api/[controller]")]
//    [ApiController]
//    public class AITestController : ControllerBase
//    {
//        private readonly ILLMService _llm;
//        private readonly JinaEmbeddingService _embeddingService;
//        private readonly VectorStore _store;
//        private readonly ChatMemory _memory;
//        private readonly JinaRerankerService _reranker;

//        public AITestController(
//            ILLMService llm,
//            JinaEmbeddingService embeddingService,
//            VectorStore store,
//            ChatMemory memory,
//            JinaRerankerService reranker)
//        {
//            _llm = llm;
//            _embeddingService = embeddingService;
//            _store = store;
//            _memory = memory;
//            _reranker = reranker;
//        }

//        // Basic LLM test
//        [HttpGet]
//        public async Task<string> Ask()
//        {
//            return await _llm.GenerateResponse(
//                "Explain embeddings in simple terms."
//            );
//        }

//        // Test embedding pipeline
//        [HttpGet("embed")]
//        public async Task<int> TestEmbedding()
//        {
//            var result = await _embeddingService.GenerateEmbeddings(
//                new List<string> { "What is refund policy?" });

//            return result[0].Length;
//        }

//        [HttpGet("seed")]
//        public async Task<string> Seed()
//        {
//            _store.Chunks.Clear();

//            var docs = new[]
//            {
//                "Refund policy allows returns within 7 days.",
//                "Shipping takes 3 to 5 business days.",
//                "Account password can be reset using email verification."
//            };

//            var embeddings = await _embeddingService.GenerateEmbeddings(docs.ToList());

//            for (int i = 0; i < docs.Length; i++)
//            {
//                _store.Chunks.Add(new DocumentChunk
//                {
//                    Content = docs[i],
//                    Embedding = embeddings[i]
//                });
//            }

//            return "Seeded successfully";
//        }

//        [HttpGet("ask-rag")]
//        public async Task<string> AskRag(string question)
//        {
//            if (string.IsNullOrWhiteSpace(question))
//                return "Question cannot be empty.";

//            if (!_store.Chunks.Any())
//                return "No document data available. Upload a file first.";

//            var questionEmbedding = (await _embeddingService
//                .GenerateEmbeddings(new List<string> { question }))[0];

//            // STEP 1 — Hybrid retrieval
//            var hybridResults = _store.Chunks
//                .Select(chunk =>
//                {
//                    var vectorScore = VectorStore.CosineSimilarity(questionEmbedding, chunk.Embedding);
//                    var keywordScore = KeywordScore(question, chunk.Content);

//                    var hybridScore = vectorScore + (keywordScore * 0.15);

//                    return new
//                    {
//                        Chunk = chunk,
//                        Score = hybridScore,
//                        VectorScore = vectorScore,
//                        KeywordScore = keywordScore
//                    };
//                })
//                .OrderByDescending(x => x.Score)
//                .Take(20) // retrieve more candidates
//                .ToList();

//            float threshold = 0.35f;

//            var candidateChunks = hybridResults
//                .Where(x => x.VectorScore >= threshold || x.KeywordScore > 0)
//                .Select(x => x.Chunk.Content)
//                .ToList();

//            if (!candidateChunks.Any())
//            {
//                return "This question is outside the scope of the uploaded documents.";
//            }

//            // STEP 2 — Rerank with Jina
//            var rerankedChunks = await _reranker.Rerank(question, candidateChunks);

//            var combinedContext = string.Join("\n", rerankedChunks);

//            var history = _memory.BuildConversationHistory();

//            var prompt = $@"
//                You are an AI assistant for answering questions from company documents.

//                Rules:
//                - Use ONLY the provided context.
//                - If the answer is not present, say:
//                'I cannot find that information in the uploaded documents.'

//                Context:
//                {combinedContext}

//                Conversation History:
//                {history}

//                Question:
//                {question}

//                Answer clearly and concisely.
//            ";

//            var response = await _llm.GenerateResponse(prompt);

//            _memory.AddUserMessage(question);
//            _memory.AddAssistantMessage(response);

//            return response;
//        }

//        [HttpPost("upload")]
//        public async Task<string> UploadFile(IFormFile file)
//        {
//            _store.Chunks.Clear();

//            if (file == null || file.Length == 0)
//                return "Invalid file.";

//            if (file.Length > 5 * 1024 * 1024)
//                return "File too large. Max 5MB allowed.";

//            string text = string.Empty;

//            try
//            {
//                if (file.FileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
//                {
//                    using var reader = new StreamReader(file.OpenReadStream());
//                    text = await reader.ReadToEndAsync();
//                }
//                else if (file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
//                {
//                    using var stream = file.OpenReadStream();
//                    using var document = UglyToad.PdfPig.PdfDocument.Open(stream);

//                    var sb = new StringBuilder();

//                    foreach (var page in document.GetPages())
//                    {
//                        sb.AppendLine(page.Text);
//                    }

//                    text = sb.ToString();
//                }
//                else
//                {
//                    return "Unsupported file type. Only .txt and .pdf allowed.";
//                }

//                if (string.IsNullOrWhiteSpace(text))
//                    return "File contains no readable text.";
//            }
//            catch (Exception ex)
//            {
//                return $"Error reading file: {ex.Message}";
//            }

//            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

//            await ProcessDocument(text);

//            stopwatch.Stop();

//            return $"File processed successfully in {stopwatch.ElapsedMilliseconds} ms. Total chunks: {_store.Chunks.Count}";
//        }

//        private async Task ProcessDocument(string text)
//        {
//            var sentences = text
//                .Split(new[] { ".", "!", "?" },
//                       StringSplitOptions.RemoveEmptyEntries)
//                .Select(s => s.Trim())
//                .Where(s => !string.IsNullOrWhiteSpace(s))
//                .ToList();

//            int chunkSentenceSize = 8;
//            int overlap = 2;
//            int maxChunks = 150;

//            var chunks = new List<string>();

//            for (int i = 0; i < sentences.Count; i += (chunkSentenceSize - overlap))
//            {
//                if (chunks.Count >= maxChunks)
//                    break;

//                var chunkSentences = sentences
//                    .Skip(i)
//                    .Take(chunkSentenceSize)
//                    .ToList();

//                if (!chunkSentences.Any())
//                    break;

//                var chunkText = string.Join(". ", chunkSentences) + ".";
//                chunks.Add(chunkText);
//            }

//            var embeddings = await _embeddingService.GenerateEmbeddings(chunks);

//            for (int i = 0; i < chunks.Count; i++)
//            {
//                _store.Chunks.Add(new DocumentChunk
//                {
//                    Content = chunks[i],
//                    Embedding = embeddings[i]
//                });
//            }
//        }

//        private int KeywordScore(string query, string content)
//        {
//            var words = query.ToLower().Split(" ");

//            int score = 0;

//            foreach (var word in words)
//            {
//                if (content.ToLower().Contains(word))
//                {
//                    score++;
//                }
//            }

//            return score;
//        }
//    }
//}