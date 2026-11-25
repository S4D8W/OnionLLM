using System.Text.Json;

namespace OnionLLM.Core.Sources;

/// <summary>
/// Łańcuchy odpowiedzi w obrębie jednego transkryptu (ccusage <c>daily/response_chain.rs</c>, #2255).
/// Claude Code zapisuje każdy blok treści odpowiedzi w osobnej linii z własnym timestampem; bez <c>requestId</c>
/// bloki jednej odpowiedzi łączy łańcuch <c>parentUuid</c> przez linie bez usage i wyniki narzędzi tej odpowiedzi.
/// Prompt użytkownika albo wynik narzędzia innej odpowiedzi zaczyna nową odpowiedź. Blok dołącza do odpowiedzi tylko
/// przy tym samym ID wiadomości oraz tokenach wejścia i cache (sygnatura). Węzły i właściciele narzędzi są kluczowane
/// sesją linii, bo kopia transkryptu powtarza te same <c>uuid</c>.
/// </summary>
internal sealed class ClaudeResponseChains(string pathSession)
{
    private readonly record struct Node(bool IsBlock, string? Parent, int? Owner, int Response);

    private readonly record struct Signature(string MessageId, long Input, long CacheCreation, long CacheRead);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    private readonly List<(string Key, Signature Signature)> _responses = [];
    private readonly Dictionary<string, int> _toolUseOwners = new(StringComparer.Ordinal);

    /// <summary>Łańcuchy są śledzone dopiero po pierwszej odpowiedzi bez <c>requestId</c>.</summary>
    public bool IsTracking => _responses.Count > 0;

    /// <summary>Linia bez zużycia (lub odrzucona) jako ogniwo łańcucha (<c>ResponseChains::link</c>).</summary>
    public void Link(JsonElement line)
    {
        if (Text(line, "type") is not { } kind)
        {
            return;
        }

        List<string>? answers = null;
        if (kind == "user")
        {
            // Linia użytkownika przechodzi tylko wtedy, gdy zawiera wyłącznie wyniki narzędzi.
            if (ClaudeRecordReader.Get(ClaudeRecordReader.Get(line, "message"), "content") is not { ValueKind: JsonValueKind.Array } blocks ||
                blocks.GetArrayLength() == 0)
            {
                return;
            }

            answers = [];
            foreach (var block in blocks.EnumerateArray())
            {
                if (Text(block, "type") != "tool_result" || Text(block, "tool_use_id") is not { } answer)
                {
                    return;
                }

                answers.Add(answer);
            }
        }

        if (Text(line, "uuid") is not { } uuid || Text(line, "parentUuid") is not { } parentUuid)
        {
            return;
        }

        if (ClaudeRecordReader.Get(ClaudeRecordReader.Get(line, "message"), "usage") is { ValueKind: not JsonValueKind.Null })
        {
            return;
        }

        var session = Text(line, "sessionId") ?? pathSession;
        var parent = Scoped(session, parentUuid);
        if (!_nodes.ContainsKey(parent))
        {
            return;
        }

        int? owner = null;
        if (answers is not null)
        {
            if (ResponseOf(parent) is not { } response ||
                !answers.All(answer => _toolUseOwners.TryGetValue(Scoped(session, answer), out var toolOwner) && toolOwner == response))
            {
                return;
            }

            owner = response;
        }

        _nodes[Scoped(session, uuid)] = new Node(false, parent, owner, 0);
    }

    /// <summary>
    /// Klucz odpowiedzi dla bloku bez <c>requestId</c> (<c>ResponseChains::response_key</c>): <c>uuid</c> pierwszego bloku
    /// odpowiedzi albo null, gdy blok nie ma <c>uuid</c> i nie dołącza do żadnej odpowiedzi.
    /// </summary>
    public string? ResponseKey(JsonElement line, string session, string messageId, ClaudeUsage usage)
    {
        var uuid = Text(line, "uuid");
        var parentUuid = Text(line, "parentUuid");
        var signature = new Signature(messageId, usage.Input, usage.CacheCreation, usage.CacheRead);

        int? joined = parentUuid is null ? null : ChainedResponse(Scoped(session, parentUuid), signature);
        int index;
        if (joined is { } existing)
        {
            index = existing;
        }
        else
        {
            if (uuid is null)
            {
                return null;
            }

            index = _responses.Count;
            _responses.Add((uuid, signature));
        }

        if (ClaudeRecordReader.Get(ClaudeRecordReader.Get(line, "message"), "content") is { ValueKind: JsonValueKind.Array } blocks)
        {
            foreach (var block in blocks.EnumerateArray())
            {
                if (Text(block, "type") == "tool_use" && Text(block, "id") is { } toolUse)
                {
                    _toolUseOwners[Scoped(session, toolUse)] = index;
                }
            }
        }

        if (uuid is not null)
        {
            _nodes[Scoped(session, uuid)] = new Node(true, null, null, index);
        }

        return _responses[index].Key;
    }

    private int? ResponseOf(string current)
    {
        for (var step = 0; step <= _nodes.Count; step++)
        {
            if (!_nodes.TryGetValue(current, out var node))
            {
                return null;
            }

            if (node.IsBlock)
            {
                return node.Response;
            }

            current = node.Parent!;
        }

        return null;
    }

    private int? ChainedResponse(string current, Signature signature)
    {
        List<int>? owners = null;
        for (var step = 0; step <= _nodes.Count; step++)
        {
            if (!_nodes.TryGetValue(current, out var node))
            {
                return null;
            }

            if (node.IsBlock)
            {
                var joins = _responses[node.Response].Signature == signature &&
                            (owners is null || owners.All(owner => owner == node.Response));
                return joins ? node.Response : null;
            }

            if (node.Owner is { } owner)
            {
                (owners ??= []).Add(owner);
            }

            current = node.Parent!;
        }

        return null;
    }

    private static string Scoped(string session, string id) => session + "\u001F" + id;

    private static string? Text(JsonElement element, string name) =>
        ClaudeRecordReader.Get(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
}
