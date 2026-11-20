var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Lưu tạm trong RAM (sẽ thay bằng Database + History Worker ở các chương sau)
var conversations = new List<ConversationDto>();
var messages = new List<MessageDto>();

// Health check
app.MapGet("/", () => Results.Ok(new
{
    service = "Chat.Api",
    status = "running"
}));

// 1. POST /conversations - Tạo phiên hỗ trợ mới (trạng thái Waiting)
app.MapPost("/conversations", (CreateConversationRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.CustomerId) ||
        string.IsNullOrWhiteSpace(request.CustomerName))
    {
        return Results.BadRequest(new ApiError(
            "INVALID_CONVERSATION",
            "Thieu ma khach hang hoac ten khach hang."));
    }

    var conversation = new ConversationDto(
        Id: Guid.NewGuid(),
        CustomerId: request.CustomerId.Trim(),
        CustomerName: request.CustomerName.Trim(),
        AgentId: null,
        AgentName: null,
        Status: "Waiting",
        CreatedAt: DateTimeOffset.UtcNow,
        ClosedAt: null);

    conversations.Add(conversation);
    return Results.Created($"/conversations/{conversation.Id}", conversation);
});

// 2. GET /conversations - Lấy danh sách phiên (có thể lọc theo ?status=Closed)
app.MapGet("/conversations", (string? status) =>
{
    var result = string.IsNullOrWhiteSpace(status)
        ? conversations
        : conversations
            .Where(c => c.Status.Equals(status.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

    return Results.Ok(result.OrderByDescending(c => c.CreatedAt));
});

// 3. GET /conversations/{id} - Tra cứu chi tiết một phiên
app.MapGet("/conversations/{id:guid}", (Guid id) =>
{
    var conversation = conversations.FirstOrDefault(c => c.Id == id);
    return conversation is null
        ? Results.NotFound(new ApiError("CONVERSATION_NOT_FOUND", "Khong tim thay phien ho tro."))
        : Results.Ok(conversation);
});

// 4. GET /conversations/{id}/messages - Tra cứu lịch sử tin nhắn của phiên
app.MapGet("/conversations/{id:guid}/messages", (Guid id) =>
{
    var exists = conversations.Any(c => c.Id == id);
    if (!exists)
    {
        return Results.NotFound(new ApiError("CONVERSATION_NOT_FOUND", "Khong tim thay phien ho tro."));
    }

    var history = messages
        .Where(m => m.ConversationId == id)
        .OrderBy(m => m.SentAt)
        .ToList();

    return Results.Ok(history);
});

// 5. POST /conversations/{id}/close - Đóng phiên hỗ trợ (chuyển sang Closed)
app.MapPost("/conversations/{id:guid}/close", (Guid id) =>
{
    var index = conversations.FindIndex(c => c.Id == id);
    if (index < 0)
    {
        return Results.NotFound(new ApiError("CONVERSATION_NOT_FOUND", "Khong tim thay phien ho tro."));
    }

    var current = conversations[index];
    if (current.Status == "Closed")
    {
        return Results.BadRequest(new ApiError("ALREADY_CLOSED", "Phien ho tro nay da dong truoc do."));
    }

    var closed = current with
    {
        Status = "Closed",
        ClosedAt = DateTimeOffset.UtcNow
    };

    conversations[index] = closed;

    // [Chương sau] Phát sự kiện ConversationClosed vào RabbitMQ và thông báo qua SignalR
    return Results.Ok(closed);
});

// --- 2 ENDPOINT BỔ TRỢ ĐỂ TEST LUỒNG TRONG CHƯƠNG 2 ---

// Nhân viên tư vấn tham gia phiên (Waiting -> Active)
app.MapPost("/conversations/{id:guid}/join", (Guid id, JoinConversationRequest request) =>
{
    var index = conversations.FindIndex(c => c.Id == id);
    if (index < 0)
    {
        return Results.NotFound(new ApiError("CONVERSATION_NOT_FOUND", "Khong tim thay phien ho tro."));
    }

    if (string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.AgentName))
    {
        return Results.BadRequest(new ApiError("INVALID_AGENT", "Thieu thong tin nhan vien tu van."));
    }

    var current = conversations[index];
    if (current.Status != "Waiting")
    {
        return Results.BadRequest(new ApiError("INVALID_STATE", "Chi co the tham gia phien dang o trang thai Waiting."));
    }

    var active = current with
    {
        AgentId = request.AgentId.Trim(),
        AgentName = request.AgentName.Trim(),
        Status = "Active"
    };

    conversations[index] = active;
    return Results.Ok(active);
});

// Gửi tin nhắn vào phiên (sau này sẽ thay bằng SignalR Hub + đẩy RabbitMQ)
app.MapPost("/conversations/{id:guid}/messages", (Guid id, SendMessageRequest request) =>
{
    var conversation = conversations.FirstOrDefault(c => c.Id == id);
    if (conversation is null)
    {
        return Results.NotFound(new ApiError("CONVERSATION_NOT_FOUND", "Khong tim thay phien ho tro."));
    }

    if (conversation.Status == "Closed")
    {
        return Results.BadRequest(new ApiError("CONVERSATION_CLOSED", "Khong the gui tin nhan vao phien da dong."));
    }

    if (string.IsNullOrWhiteSpace(request.SenderId) || string.IsNullOrWhiteSpace(request.Content))
    {
        return Results.BadRequest(new ApiError("INVALID_MESSAGE", "Thieu nguoi gui hoac noi dung tin nhan."));
    }

    var msg = new MessageDto(
        Id: Guid.NewGuid(),
        ConversationId: id,
        SenderId: request.SenderId.Trim(),
        SenderRole: string.IsNullOrWhiteSpace(request.SenderRole) ? "Customer" : request.SenderRole.Trim(),
        Content: request.Content.Trim(),
        SentAt: DateTimeOffset.UtcNow);

    messages.Add(msg);

    // [Chương sau] Phát sự kiện MessageSent vào RabbitMQ cho History Worker lưu DB
    return Results.Created($"/conversations/{id}/messages", msg);
});

app.Run();

// Khai báo các DTO (tách biệt dữ liệu đầu vào, đầu ra và lỗi)
record CreateConversationRequest(string CustomerId, string CustomerName);
record JoinConversationRequest(string AgentId, string AgentName);
record SendMessageRequest(string SenderId, string SenderRole, string Content);

record ConversationDto(
    Guid Id,
    string CustomerId,
    string CustomerName,
    string? AgentId,
    string? AgentName,
    string Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ClosedAt);

record MessageDto(
    Guid Id,
    Guid ConversationId,
    string SenderId,
    string SenderRole,
    string Content,
    DateTimeOffset SentAt);

record ApiError(string Code, string Message);