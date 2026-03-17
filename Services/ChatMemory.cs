using LocalRagAPI.Models;
using System.Collections.Concurrent;

namespace LocalRagAPI.Services
{
    // ChatMemory stores messages per user+session key to isolate conversation history
    public class ChatMemory
    {
        // key: "{userId}|{sessionId}" where Guid.Empty is allowed for anonymous/local
        private readonly ConcurrentDictionary<string, List<ChatMessage>> _memories = new();

        private string Key(Guid userId, Guid sessionId) => $"{userId:N}|{sessionId:N}";

        private List<ChatMessage> GetOrCreate(Guid userId, Guid sessionId)
        {
            var key = Key(userId, sessionId);
            return _memories.GetOrAdd(key, _ => new List<ChatMessage>());
        }

        public void AddUserMessage(Guid userId, Guid sessionId, string message)
        {
            var list = GetOrCreate(userId, sessionId);
            lock (list)
            {
                list.Add(new ChatMessage { Role = "user", Content = message });
                if (list.Count > 200) // simple cap
                    list.RemoveRange(0, list.Count - 200);
            }
        }

        public void AddAssistantMessage(Guid userId, Guid sessionId, string message)
        {
            var list = GetOrCreate(userId, sessionId);
            lock (list)
            {
                list.Add(new ChatMessage { Role = "assistant", Content = message });
                if (list.Count > 200)
                    list.RemoveRange(0, list.Count - 200);
            }
        }

        public string BuildConversationHistory(Guid userId, Guid sessionId)
        {
            var list = GetOrCreate(userId, sessionId);
            lock (list)
            {
                return string.Join('\n', list.Select(m => $"{m.Role}: {m.Content}"));
            }
        }

        public void Clear(Guid userId, Guid sessionId)
        {
            var key = Key(userId, sessionId);
            _memories.TryRemove(key, out _);
        }
    }
}

