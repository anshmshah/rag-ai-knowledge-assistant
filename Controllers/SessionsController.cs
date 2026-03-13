using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using LocalRagAPI.Repositories;
using LocalRagAPI.Models;
using System.Linq;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class SessionsController : ControllerBase
    {
        private readonly IChatSessionRepository _sessions;
        private readonly IMessageRepository _messages;

        public SessionsController(IChatSessionRepository sessions, IMessageRepository messages)
        {
            _sessions = sessions;
            _messages = messages;
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

        // GET /api/sessions
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = GetCurrentUserId();
            var sessions = await _sessions.ListByUserAsync(userId);

            var result = sessions.Select(s => new
            {
                id = s.Id,
                title = s.Title,
                createdAt = s.CreatedAt,
                expiresAt = s.ExpiresAt
            });

            return Ok(result);
        }

        // POST /api/sessions
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateSessionRequest req)
        {
            var userId = GetCurrentUserId();

            var session = new ChatSession
            {
                UserId = userId,
                Title = string.IsNullOrWhiteSpace(req?.Title) ? "Chat" : req.Title,
                ExpiresAt = DateTime.UtcNow.AddDays(req?.ExpiresInDays ?? 30)
            };

            var created = await _sessions.CreateAsync(session);

            return Ok(new { id = created.Id, title = created.Title, createdAt = created.CreatedAt, expiresAt = created.ExpiresAt });
        }

        // GET /api/sessions/{id}
        [HttpGet("{id}")]
        public async Task<IActionResult> Get(Guid id)
        {
            var userId = GetCurrentUserId();

            var session = await _sessions.GetByIdAsync(id);
            if (session == null) return NotFound();

            if (session.UserId != userId) return Forbid();

            var msgs = await _messages.ListBySessionAsync(session.Id);
            var result = msgs.Select(m => new { id = m.Id, role = m.Role, content = m.Content, createdAt = m.CreatedAt });

            return Ok(new { session = new { id = session.Id, title = session.Title, createdAt = session.CreatedAt, expiresAt = session.ExpiresAt }, messages = result });
        }

        public class CreateSessionRequest
        {
            public string Title { get; set; }
            public int? ExpiresInDays { get; set; }
        }
    }
}
