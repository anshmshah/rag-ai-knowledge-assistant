using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using LocalRagAPI.Models;
using LocalRagAPI.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Microsoft.AspNetCore.Identity;

namespace LocalRagAPI.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class AuthController : ControllerBase
    {
        private readonly IUserRepository _users;
        private readonly IConfiguration _config;
        private readonly PasswordHasher<User> _passwordHasher = new PasswordHasher<User>();

        public AuthController(IUserRepository users, IConfiguration config)
        {
            _users = users;
            _config = config;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { error = "Email and password are required" });

            var existing = await _users.GetByEmailAsync(req.Email);
            if (existing != null) return Conflict(new { error = "User already exists" });

            var user = new User
            {
                Email = req.Email
            };

            user.PasswordHash = _passwordHasher.HashPassword(user, req.Password);

            await _users.CreateAsync(user);

            return Ok(new { id = user.Id, email = user.Email });
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest req)
        {
            if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Password))
                return BadRequest(new { error = "Email and password are required" });

            var user = await _users.GetByEmailAsync(req.Email);
            if (user == null) return Unauthorized(new { error = "Invalid credentials" });

            var verify = _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
            if (verify == PasswordVerificationResult.Failed) return Unauthorized(new { error = "Invalid credentials" });

            var key = _config["Jwt:Key"];
            var issuer = _config["Jwt:Issuer"] ?? "localrag";
            var audience = _config["Jwt:Audience"] ?? "localrag";

            var claims = new[] {
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                new Claim(JwtRegisteredClaimNames.Email, user.Email)
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var tokenKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key ?? "THIS_IS_A_SUPER_SECRET_KEY_FOR_LOCAL_RAG_API_2026_123456"));
            var creds = new SigningCredentials(tokenKey, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                expires: DateTime.UtcNow.AddDays(7),
                signingCredentials: creds
            );

            var tokenString = tokenHandler.WriteToken(token);

            return Ok(new { token = tokenString });
        }

        public class RegisterRequest { public string Email { get; set; } public string Password { get; set; } }
        public class LoginRequest { public string Email { get; set; } public string Password { get; set; } }
    }
}
