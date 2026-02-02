using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ControlRoom.Application.Integrations;

/// <summary>
/// Slack integration provider.
/// Supports channels, messages, users, and webhooks.
/// </summary>
public sealed class SlackProvider : ICommunicationProvider
{
    private readonly HttpClient _httpClient;
    private string? _botToken;
    private string? _userToken;

    public string ProviderName => "slack";
    public DevOpsCategory Category => DevOpsCategory.Communication;

    private const string BaseUrl = "https://slack.com/api";

    public SlackProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
    }

    /// <summary>
    /// Configure with bot token and optional user token.
    /// </summary>
    public void Configure(string botToken, string? userToken = null)
    {
        _botToken = botToken;
        _userToken = userToken;
    }

    public async Task<DevOpsValidationResult> ValidateCredentialsAsync(
        Dictionary<string, string> configuration,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.TryGetValue("bot_token", out var botToken))
        {
            return new DevOpsValidationResult(
                false, "Missing required bot_token", null, null, null, null, []);
        }

        Configure(botToken, configuration.GetValueOrDefault("user_token"));

        try
        {
            var response = await MakeRequestAsync("auth.test", null, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var authResult = JsonSerializer.Deserialize<SlackAuthResponse>(content);

            if (authResult?.Ok != true)
            {
                return new DevOpsValidationResult(
                    false, $"Authentication failed: {authResult?.Error}",
                    null, null, null, null, []);
            }

            // Get bot info for scopes
            var scopesResponse = await MakeRequestAsync("auth.test", null, cancellationToken);
            var scopesContent = await scopesResponse.Content.ReadAsStringAsync(cancellationToken);

            return new DevOpsValidationResult(
                true,
                null,
                authResult.UserId,
                authResult.User,
                authResult.TeamId,
                authResult.Team,
                ["chat:write", "channels:read", "users:read"],
                new() { ["url"] = authResult.Url ?? "" });
        }
        catch (Exception ex)
        {
            return new DevOpsValidationResult(
                false, $"Validation failed: {ex.Message}", null, null, null, null, []);
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await MakeRequestAsync("api.test", null, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<SlackResponse>(content);
            return result?.Ok == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<DevOpsResourceList<Channel>> ListChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var allChannels = new List<Channel>();
        string? cursor = null;

        do
        {
            var parameters = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["limit"] = "200"
            };

            if (cursor != null)
                parameters["cursor"] = cursor;

            var response = await MakeRequestAsync("conversations.list", parameters, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<SlackChannelsResponse>(content);

            if (result?.Ok != true)
                throw new InvalidOperationException($"Failed to list channels: {result?.Error}");

            allChannels.AddRange((result.Channels ?? []).Select(MapChannel));
            cursor = result.ResponseMetadata?.NextCursor;

        } while (!string.IsNullOrEmpty(cursor));

        return new DevOpsResourceList<Channel>(
            allChannels,
            allChannels.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    public async Task<Channel?> GetChannelAsync(
        string channelId,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var parameters = new Dictionary<string, string>
        {
            ["channel"] = channelId
        };

        var response = await MakeRequestAsync("conversations.info", parameters, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackChannelResponse>(content);

        return result?.Ok == true && result.Channel != null ? MapChannel(result.Channel) : null;
    }

    public async Task<Message> SendMessageAsync(
        string channelId,
        string text,
        MessageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["text"] = text
        };

        if (options?.ThreadId != null)
            body["thread_ts"] = options.ThreadId;

        if (options?.UnfurlLinks != null)
            body["unfurl_links"] = options.UnfurlLinks.Value;

        if (options?.UnfurlMedia != null)
            body["unfurl_media"] = options.UnfurlMedia.Value;

        if (options?.Attachments != null)
        {
            body["attachments"] = options.Attachments.Select(a => new
            {
                title = a.Title,
                text = a.Text,
                color = a.Color,
                image_url = a.ImageUrl,
                fields = a.Fields?.Select(f => new
                {
                    title = f.Title,
                    value = f.Value,
                    @short = f.IsShort
                })
            });
        }

        var response = await MakeRequestAsync(
            "chat.postMessage",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackPostMessageResponse>(content);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to send message: {result?.Error}");

        return MapMessage(result.Message!, channelId);
    }

    public async Task<Message> UpdateMessageAsync(
        string channelId,
        string messageId,
        string text,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["ts"] = messageId,
            ["text"] = text
        };

        var response = await MakeRequestAsync(
            "chat.update",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackPostMessageResponse>(content);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to update message: {result?.Error}");

        return MapMessage(result.Message!, channelId);
    }

    public async Task<Message> SendDirectMessageAsync(
        string userId,
        string text,
        MessageOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        // Open DM conversation first
        var openBody = new Dictionary<string, object>
        {
            ["users"] = userId
        };

        var openResponse = await MakeRequestAsync(
            "conversations.open",
            null,
            cancellationToken,
            JsonSerializer.Serialize(openBody));

        var openContent = await openResponse.Content.ReadAsStringAsync(cancellationToken);
        var openResult = JsonSerializer.Deserialize<SlackConversationOpenResponse>(openContent);

        if (openResult?.Ok != true || openResult.Channel?.Id == null)
            throw new InvalidOperationException($"Failed to open DM: {openResult?.Error}");

        return await SendMessageAsync(openResult.Channel.Id, text, options, cancellationToken);
    }

    public async Task<DevOpsResourceList<ChatUser>> ListUsersAsync(
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var allUsers = new List<ChatUser>();
        string? cursor = null;

        do
        {
            var parameters = new Dictionary<string, string>
            {
                ["limit"] = "200"
            };

            if (cursor != null)
                parameters["cursor"] = cursor;

            var response = await MakeRequestAsync("users.list", parameters, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<SlackUsersResponse>(content);

            if (result?.Ok != true)
                throw new InvalidOperationException($"Failed to list users: {result?.Error}");

            allUsers.AddRange((result.Members ?? [])
                .Where(u => u.Deleted != true)
                .Select(MapUser));

            cursor = result.ResponseMetadata?.NextCursor;

        } while (!string.IsNullOrEmpty(cursor));

        return new DevOpsResourceList<ChatUser>(
            allUsers,
            allUsers.Count,
            null,
            DateTimeOffset.UtcNow);
    }

    // ========================================================================
    // Slack-Specific Operations
    // ========================================================================

    /// <summary>
    /// Sends a message using Block Kit.
    /// </summary>
    public async Task<Message> SendBlockMessageAsync(
        string channelId,
        IReadOnlyList<object> blocks,
        string? text = null,
        string? threadId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["blocks"] = blocks
        };

        if (text != null)
            body["text"] = text;

        if (threadId != null)
            body["thread_ts"] = threadId;

        var response = await MakeRequestAsync(
            "chat.postMessage",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackPostMessageResponse>(content);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to send block message: {result?.Error}");

        return MapMessage(result.Message!, channelId);
    }

    /// <summary>
    /// Adds a reaction to a message.
    /// </summary>
    public async Task AddReactionAsync(
        string channelId,
        string messageId,
        string emoji,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["timestamp"] = messageId,
            ["name"] = emoji.Trim(':')
        };

        var response = await MakeRequestAsync(
            "reactions.add",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackResponse>(content);

        if (result?.Ok != true && result?.Error != "already_reacted")
            throw new InvalidOperationException($"Failed to add reaction: {result?.Error}");
    }

    /// <summary>
    /// Uploads a file to a channel.
    /// </summary>
    public async Task<SlackFile> UploadFileAsync(
        string channelId,
        string filename,
        byte[] content,
        string? title = null,
        string? initialComment = null,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        using var formData = new MultipartFormDataContent();
        formData.Add(new StringContent(channelId), "channels");
        formData.Add(new ByteArrayContent(content), "file", filename);

        if (title != null)
            formData.Add(new StringContent(title), "title");

        if (initialComment != null)
            formData.Add(new StringContent(initialComment), "initial_comment");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/files.upload");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        request.Content = formData;

        var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseContent = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackFileResponse>(responseContent);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to upload file: {result?.Error}");

        return result.File!;
    }

    /// <summary>
    /// Creates a channel.
    /// </summary>
    public async Task<Channel> CreateChannelAsync(
        string name,
        bool isPrivate = false,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["name"] = name,
            ["is_private"] = isPrivate
        };

        var response = await MakeRequestAsync(
            "conversations.create",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackChannelResponse>(content);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to create channel: {result?.Error}");

        return MapChannel(result.Channel!);
    }

    /// <summary>
    /// Invites users to a channel.
    /// </summary>
    public async Task InviteToChannelAsync(
        string channelId,
        IReadOnlyList<string> userIds,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["users"] = string.Join(",", userIds)
        };

        var response = await MakeRequestAsync(
            "conversations.invite",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackResponse>(content);

        if (result?.Ok != true && result?.Error != "already_in_channel")
            throw new InvalidOperationException($"Failed to invite users: {result?.Error}");
    }

    /// <summary>
    /// Sets the channel topic.
    /// </summary>
    public async Task SetChannelTopicAsync(
        string channelId,
        string topic,
        CancellationToken cancellationToken = default)
    {
        ValidateConfiguration();

        var body = new Dictionary<string, object>
        {
            ["channel"] = channelId,
            ["topic"] = topic
        };

        var response = await MakeRequestAsync(
            "conversations.setTopic",
            null,
            cancellationToken,
            JsonSerializer.Serialize(body));

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlackResponse>(content);

        if (result?.Ok != true)
            throw new InvalidOperationException($"Failed to set topic: {result?.Error}");
    }

    /// <summary>
    /// Posts a message via incoming webhook URL.
    /// </summary>
    public async Task PostWebhookAsync(
        string webhookUrl,
        string text,
        IReadOnlyList<MessageAttachment>? attachments = null,
        IReadOnlyList<object>? blocks = null,
        CancellationToken cancellationToken = default)
    {
        var body = new Dictionary<string, object>
        {
            ["text"] = text
        };

        if (attachments != null)
        {
            body["attachments"] = attachments.Select(a => new
            {
                title = a.Title,
                text = a.Text,
                color = a.Color,
                image_url = a.ImageUrl
            });
        }

        if (blocks != null)
        {
            body["blocks"] = blocks;
        }

        var request = new HttpRequestMessage(HttpMethod.Post, webhookUrl);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // ========================================================================
    // Private Helpers
    // ========================================================================

    private void ValidateConfiguration()
    {
        if (string.IsNullOrEmpty(_botToken))
        {
            throw new InvalidOperationException("Slack bot token not configured. Call Configure() first.");
        }
    }

    private async Task<HttpResponseMessage> MakeRequestAsync(
        string method,
        Dictionary<string, string>? parameters,
        CancellationToken cancellationToken,
        string? body = null)
    {
        HttpRequestMessage request;

        if (body != null)
        {
            request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/{method}");
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        else
        {
            var queryString = parameters != null && parameters.Count > 0
                ? "?" + string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"))
                : "";
            request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/{method}{queryString}");
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _botToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static Channel MapChannel(SlackChannel channel)
    {
        return new Channel(
            channel.Id ?? "",
            channel.Name ?? "",
            channel.Topic?.Value,
            channel.Purpose?.Value,
            channel.IsPrivate,
            channel.IsArchived,
            channel.NumMembers,
            DateTimeOffset.FromUnixTimeSeconds(channel.Created));
    }

    private static Message MapMessage(SlackMessage message, string channelId)
    {
        var reactions = message.Reactions?.Select(r => new MessageReaction(
            r.Name ?? "",
            r.Count,
            r.Users ?? []
        )).ToList();

        return new Message(
            message.Ts ?? "",
            channelId,
            message.Text ?? "",
            null, // Author needs separate lookup
            DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(message.Ts ?? "0")),
            message.Edited != null ? DateTimeOffset.FromUnixTimeSeconds((long)double.Parse(message.Edited.Ts ?? "0")) : null,
            null,
            reactions,
            message.ThreadTs,
            message.ReplyCount);
    }

    private static ChatUser MapUser(SlackUser user)
    {
        var presence = user.Presence switch
        {
            "active" => ChatUserPresence.Active,
            "away" => ChatUserPresence.Away,
            _ => ChatUserPresence.Offline
        };

        return new ChatUser(
            user.Id ?? "",
            user.Name ?? "",
            user.Profile?.DisplayName ?? user.RealName,
            user.Profile?.Email,
            user.Profile?.Image48,
            user.IsBot,
            presence);
    }
}

// ========================================================================
// Slack DTOs
// ========================================================================

internal class SlackResponse
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

internal sealed class SlackAuthResponse : SlackResponse
{
    [JsonPropertyName("user_id")]
    public string? UserId { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("team_id")]
    public string? TeamId { get; set; }

    [JsonPropertyName("team")]
    public string? Team { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }
}

internal sealed class SlackChannelsResponse : SlackResponse
{
    [JsonPropertyName("channels")]
    public List<SlackChannel>? Channels { get; set; }

    [JsonPropertyName("response_metadata")]
    public SlackResponseMetadata? ResponseMetadata { get; set; }
}

internal sealed class SlackChannelResponse : SlackResponse
{
    [JsonPropertyName("channel")]
    public SlackChannel? Channel { get; set; }
}

internal sealed class SlackChannel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("is_private")]
    public bool IsPrivate { get; set; }

    [JsonPropertyName("is_archived")]
    public bool IsArchived { get; set; }

    [JsonPropertyName("num_members")]
    public int NumMembers { get; set; }

    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("topic")]
    public SlackTopicPurpose? Topic { get; set; }

    [JsonPropertyName("purpose")]
    public SlackTopicPurpose? Purpose { get; set; }
}

internal sealed class SlackTopicPurpose
{
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

internal sealed class SlackPostMessageResponse : SlackResponse
{
    [JsonPropertyName("message")]
    public SlackMessage? Message { get; set; }

    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("channel")]
    public string? Channel { get; set; }
}

internal sealed class SlackMessage
{
    [JsonPropertyName("ts")]
    public string? Ts { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("thread_ts")]
    public string? ThreadTs { get; set; }

    [JsonPropertyName("reply_count")]
    public int ReplyCount { get; set; }

    [JsonPropertyName("edited")]
    public SlackEdited? Edited { get; set; }

    [JsonPropertyName("reactions")]
    public List<SlackReaction>? Reactions { get; set; }
}

internal sealed class SlackEdited
{
    [JsonPropertyName("ts")]
    public string? Ts { get; set; }
}

internal sealed class SlackReaction
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("count")]
    public int Count { get; set; }

    [JsonPropertyName("users")]
    public List<string>? Users { get; set; }
}

internal sealed class SlackUsersResponse : SlackResponse
{
    [JsonPropertyName("members")]
    public List<SlackUser>? Members { get; set; }

    [JsonPropertyName("response_metadata")]
    public SlackResponseMetadata? ResponseMetadata { get; set; }
}

internal sealed class SlackUser
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("real_name")]
    public string? RealName { get; set; }

    [JsonPropertyName("deleted")]
    public bool Deleted { get; set; }

    [JsonPropertyName("is_bot")]
    public bool IsBot { get; set; }

    [JsonPropertyName("presence")]
    public string? Presence { get; set; }

    [JsonPropertyName("profile")]
    public SlackProfile? Profile { get; set; }
}

internal sealed class SlackProfile
{
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    [JsonPropertyName("image_48")]
    public string? Image48 { get; set; }
}

internal sealed class SlackResponseMetadata
{
    [JsonPropertyName("next_cursor")]
    public string? NextCursor { get; set; }
}

internal sealed class SlackConversationOpenResponse : SlackResponse
{
    [JsonPropertyName("channel")]
    public SlackChannel? Channel { get; set; }
}

internal sealed class SlackFileResponse : SlackResponse
{
    [JsonPropertyName("file")]
    public SlackFile? File { get; set; }
}

/// <summary>
/// Slack file.
/// </summary>
public sealed class SlackFile
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("filetype")]
    public string? FileType { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("url_private")]
    public string? UrlPrivate { get; set; }

    [JsonPropertyName("permalink")]
    public string? Permalink { get; set; }
}
