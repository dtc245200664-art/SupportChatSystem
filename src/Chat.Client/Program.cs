using System.Net;
using System.Net.Http.Json;

var apiBaseUrl = args.Length > 0 ? args[0] : "http://localhost:5050";
using var http = new HttpClient { BaseAddress = new Uri(apiBaseUrl) };

// Bước 1: Khách hàng tạo phiên hỗ trợ mới
Console.WriteLine("=== 1. Tao phien ho tro moi ===");
var createRes = await http.PostAsJsonAsync("/conversations",
    new CreateConversationRequest("CUS01", "Nguyen Van A"));

if (createRes.StatusCode != HttpStatusCode.Created)
{
    await PrintApiErrorAsync(createRes);
    return;
}

var conv = await createRes.Content.ReadFromJsonAsync<ConversationDto>();
Console.WriteLine($"Da tao phien: {conv?.Id} | Trang thai: {conv?.Status}");

// Bước 2: Nhân viên tư vấn tham gia phiên (Waiting -> Active)
Console.WriteLine("\n=== 2. Nhan vien tham gia phien ===");
var joinRes = await http.PostAsJsonAsync($"/conversations/{conv?.Id}/join",
    new JoinConversationRequest("AGT01", "Tu van vien Lan"));
var activeConv = await joinRes.Content.ReadFromJsonAsync<ConversationDto>();
Console.WriteLine($"Trang thai moi: {activeConv?.Status} | Nhan vien: {activeConv?.AgentName}");

// Bước 3: Trao đổi 2 tin nhắn mẫu
Console.WriteLine("\n=== 3. Gui tin nhan trong phien ===");
await http.PostAsJsonAsync($"/conversations/{conv?.Id}/messages",
    new SendMessageRequest("CUS01", "Customer", "Chao shop, don hang cua toi den dau roi?"));
await http.PostAsJsonAsync($"/conversations/{conv?.Id}/messages",
    new SendMessageRequest("AGT01", "Agent", "Chao ban, minh kiem tra ngay cho ban nhe!"));

// Bước 4: Đóng phiên hỗ trợ (Active -> Closed)
Console.WriteLine("\n=== 4. Dong phien ho tro ===");
var closeRes = await http.PostAsync($"/conversations/{conv?.Id}/close", null);
var closedConv = await closeRes.Content.ReadFromJsonAsync<ConversationDto>();
Console.WriteLine($"Da dong phien: {closedConv?.Id} | Trang thai: {closedConv?.Status}");

// Bước 5: Tra cứu lịch sử tin nhắn của phiên đã đóng
Console.WriteLine("\n=== 5. Tra cuu lich su tin nhan ===");
var history = await http.GetFromJsonAsync<List<MessageDto>>($"/conversations/{conv?.Id}/messages");
foreach (var m in history ?? [])
{
    Console.WriteLine($"[{m.SenderRole}] {m.Content}");
}

// Bước 6: Cố tình gửi tin nhắn vào phiên đã đóng để kiểm tra lỗi 400
Console.WriteLine("\n=== 6. Thu gui tin nhan vao phien da dong (400 BadRequest) ===");
var invalidRes = await http.PostAsJsonAsync($"/conversations/{conv?.Id}/messages",
    new SendMessageRequest("CUS01", "Customer", "Alo con ai khong?"));
Console.WriteLine($"Ma trang thai: {(int)invalidRes.StatusCode} {invalidRes.StatusCode}");
await PrintApiErrorAsync(invalidRes);

static async Task PrintApiErrorAsync(HttpResponseMessage response)
{
    var error = await response.Content.ReadFromJsonAsync<ApiError>();
    Console.WriteLine($"Loi [{error?.Code}]: {error?.Message}");
}

record CreateConversationRequest(string CustomerId, string CustomerName);
record JoinConversationRequest(string AgentId, string AgentName);
record SendMessageRequest(string SenderId, string SenderRole, string Content);
record ConversationDto(Guid Id, string CustomerId, string CustomerName, string? AgentId, string? AgentName, string Status, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);
record MessageDto(Guid Id, Guid ConversationId, string SenderId, string SenderRole, string Content, DateTimeOffset SentAt);
record ApiError(string Code, string Message);