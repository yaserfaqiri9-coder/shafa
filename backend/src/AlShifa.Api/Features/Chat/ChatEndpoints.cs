using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using AlShifa.Api.Features.Users;
using AlShifa.Api.Infrastructure;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using AlShifa.Api.Features.Realtime;

namespace AlShifa.Api.Features.Chat;

public sealed record SendChatMessageRequest(long? RecipientUserId, [Required, MaxLength(64)] string ClientMessageId,
    [Required, MaxLength(2000)] string Content);
public sealed record ChatUserDto(long Id, string FullName, string UserName);
public sealed record SendPublicChatMessageRequest([Required, MaxLength(64)] string ClientMessageId,
    [Required, MaxLength(2000)] string Content);
public sealed record ChatMessageDto(long Id, long SenderUserId, long? RecipientUserId, string SenderName,
    bool IsPublic, string Content, DateTime CreatedAt);

public static class ChatEndpoints
{
    public static void MapChat(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/api/chat").WithTags("Chat").RequireAuthorization();

        chat.MapGet("/users", async (HttpContext http, AppDbContext db, string? search, CancellationToken ct) =>
        {
            var currentUserId = UserId(http);
            var users = db.Users.AsNoTracking().Where(user => user.IsActive && user.Id != currentUserId);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                users = users.Where(user => user.FullName.Contains(term) || user.UserName!.Contains(term));
            }

            return Results.Ok(await users.OrderBy(user => user.FullName).ThenBy(user => user.UserName)
                .Take(100).Select(user => new ChatUserDto(user.Id, user.FullName, user.UserName!)).ToListAsync(ct));
        });

        chat.MapGet("/{otherUserId:long}/messages", async (long otherUserId, HttpContext http, AppDbContext db, CancellationToken ct) =>
        {
            var currentUserId = UserId(http);
            await RequireActivePair(db, currentUserId, otherUserId, ct);

            var messages = await (from message in db.ChatMessages.AsNoTracking()
                join sender in db.Users.AsNoTracking() on message.SenderUserId equals sender.Id
                where (message.SenderUserId == currentUserId && message.RecipientUserId == otherUserId)
                    || (message.SenderUserId == otherUserId && message.RecipientUserId == currentUserId)
                orderby message.Id descending
                select new ChatMessageDto(message.Id, message.SenderUserId, message.RecipientUserId,
                    sender.FullName, false, message.Content, message.CreatedAt)).Take(100)
                .ToListAsync(ct);
            messages.Reverse();
            return Results.Ok(messages);
        });

        chat.MapGet("/public/messages", async (AppDbContext db, CancellationToken ct) =>
        {
            var messages = await (from message in db.ChatMessages.AsNoTracking()
                join sender in db.Users.AsNoTracking() on message.SenderUserId equals sender.Id
                where message.RecipientUserId == null
                orderby message.Id descending
                select new ChatMessageDto(message.Id, message.SenderUserId, null, sender.FullName,
                    true, message.Content, message.CreatedAt)).Take(100).ToListAsync(ct);
            messages.Reverse();
            return Results.Ok(messages);
        });

        chat.MapPost("/messages", async (SendChatMessageRequest request, HttpContext http, AppDbContext db,
            IHubContext<EventsHub> hub, CancellationToken ct) =>
        {
            var senderId = UserId(http);
            if (request.RecipientUserId is not long recipientId)
                throw new AppException(400, "recipient_required", "گیرنده پیام را انتخاب کنید.");
            await RequireActivePair(db, senderId, recipientId, ct);
            return await SaveMessageAsync(senderId, recipientId, request.ClientMessageId,
                request.Content, db, hub, ct);
        });

        chat.MapPost("/public/messages", async (SendPublicChatMessageRequest request, HttpContext http,
            AppDbContext db, IHubContext<EventsHub> hub, CancellationToken ct) =>
            await SaveMessageAsync(UserId(http), null, request.ClientMessageId, request.Content, db, hub, ct));
    }

    private static async Task<IResult> SaveMessageAsync(long senderId, long? recipientId, string clientMessageId,
        string text, AppDbContext db, IHubContext<EventsHub> hub, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientMessageId) || clientMessageId.Length > 64)
            throw new AppException(400, "invalid_message_id", "شناسه پیام درست نیست.");
        var content = text.Trim();
        if (content.Length is 0 or > 2000)
            throw new AppException(400, "invalid_message", "پیام باید بین ۱ تا ۲۰۰۰ نویسه باشد.");

        var previous = await db.ChatMessages.AsNoTracking().SingleOrDefaultAsync(message =>
            message.SenderUserId == senderId && message.ClientMessageId == clientMessageId, ct);
        if (previous is not null)
        {
            var previousSender = await db.Users.AsNoTracking().Where(user => user.Id == senderId)
                .Select(user => user.FullName).SingleAsync(ct);
            return Results.Ok(new ChatMessageDto(previous.Id, previous.SenderUserId, previous.RecipientUserId,
                previousSender, previous.RecipientUserId == null, previous.Content, previous.CreatedAt));
        }

        var senderName = await db.Users.AsNoTracking().Where(user => user.Id == senderId)
            .Select(user => user.FullName).SingleOrDefaultAsync(ct) ?? throw AppException.NotFound();
        var message = new ChatMessage
        {
            SenderUserId = senderId,
            RecipientUserId = recipientId,
            ClientMessageId = clientMessageId,
            Content = content,
            CreatedAt = DateTime.UtcNow,
        };
        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);

        var dto = new ChatMessageDto(message.Id, message.SenderUserId, message.RecipientUserId,
            senderName, recipientId == null, message.Content, message.CreatedAt);
        try
        {
            if (recipientId is null)
                await hub.Clients.All.SendAsync("chatMessage", dto, ct);
            else
            {
                await hub.Clients.User(recipientId.Value.ToString()).SendAsync("chatMessage", dto, ct);
                await hub.Clients.User(senderId.ToString()).SendAsync("chatMessage", dto, ct);
            }
        }
        catch
        {
            // The message is committed. Clients reload from the API if a live notification is missed.
        }

        var location = recipientId is null ? "/api/chat/public/messages" : $"/api/chat/{recipientId}/messages";
        return Results.Created(location, dto);
    }

    private static long UserId(HttpContext http) =>
        long.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static async Task RequireActivePair(AppDbContext db, long currentUserId, long otherUserId, CancellationToken ct)
    {
        if (currentUserId == otherUserId || !await db.Users.AsNoTracking()
                .AnyAsync(user => user.Id == otherUserId && user.IsActive, ct))
            throw AppException.NotFound();
    }
}
