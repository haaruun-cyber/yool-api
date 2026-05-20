using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace aspbackend.Hubs;

[Authorize]
public sealed class CollaborationHub : Hub
{
    public Task JoinDocument(string documentId) => Groups.AddToGroupAsync(Context.ConnectionId, documentId);
    public Task LeaveDocument(string documentId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, documentId);
    public Task SendChange(string documentId, object change) => Clients.OthersInGroup(documentId).SendAsync("document-change", change);
    public Task SendCursor(string documentId, object cursor) => Clients.OthersInGroup(documentId).SendAsync("cursor-update", cursor);
}
