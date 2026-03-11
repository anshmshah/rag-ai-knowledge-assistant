using LocalRagAPI.Models;

namespace LocalRagAPI.Services
{
    public class ChatMemory
    {
        public List<ChatMessage> Messages { get; } = new();

        public void AddUserMessage(string message)
        {
            Messages.Add(new ChatMessage
            {
                Role = "user",
                Content = message
            });
        }

        public void AddAssistantMessage(string message)
        {
            Messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = message
            });
        }

        public string BuildConversationHistory()
        {
            return string.Join("\n",
                Messages.Select(m => $"{m.Role}: {m.Content}"));
        }

        public void Clear()
        {
            Messages.Clear();
        }
    }
}

