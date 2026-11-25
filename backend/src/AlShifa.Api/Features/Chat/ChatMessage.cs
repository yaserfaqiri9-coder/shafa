using System.ComponentModel.DataAnnotations;

namespace AlShifa.Api.Features.Chat;

public sealed class ChatMessage
{
    public long Id { get; set; }

    public long SenderUserId { get; set; }

    public long? RecipientUserId { get; set; }

    [MaxLength(64)]
    public string ClientMessageId { get; set; } = "";

    [MaxLength(2000)]
    public string Content { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

}
