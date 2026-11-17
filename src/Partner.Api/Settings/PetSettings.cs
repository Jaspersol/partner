namespace Partner.Api.Settings;

/// <summary>The four emotions the keyword dictionary scores. Serialized by name so a hand-edited file stays legible.</summary>
public static class PetEmotions
{
    public const string Smile = "smile";
    public const string Angry = "angry";
    public const string Cry = "cry";
    public const string Shy = "shy";

    public static readonly IReadOnlyList<string> Values = [Smile, Angry, Cry, Shy];
}

/// <summary>One dictionary entry: the text to look for and how strongly it reads as that emotion.</summary>
public sealed record PetKeyword
{
    public string Text { get; init; } = string.Empty;

    /// <summary>1–10. A reply's emotion is the highest weight it matched, not the sum.</summary>
    public int Weight { get; init; } = 1;
}

/// <summary>The keyword dictionary. Keys are <see cref="PetEmotions"/> values.</summary>
public sealed record PetEmotionDictionary
{
    public IReadOnlyDictionary<string, IReadOnlyList<PetKeyword>> Keywords { get; init; } =
        new Dictionary<string, IReadOnlyList<PetKeyword>>(StringComparer.Ordinal);

    /// <summary>Words that invert a hit when they sit within two characters before it (<c>不开心</c> is not <c>开心</c>).</summary>
    public IReadOnlyList<string> NegationPrefixes { get; init; } =
        ["不", "没", "别", "不太", "并不", "not", "no", "never"];

    /// <summary>A hit below this weight does not trigger the emotion. Default 1: any hit counts.</summary>
    public int MinWeight { get; init; } = 1;

    /// <summary>The built-in table, restored by the page's "restore defaults".</summary>
    public static PetEmotionDictionary Default { get; } = new()
    {
        Keywords = new Dictionary<string, IReadOnlyList<PetKeyword>>(StringComparer.Ordinal)
        {
            [PetEmotions.Smile] =
            [
                new() { Text = "哈哈", Weight = 3 }, new() { Text = "嘻嘻", Weight = 3 },
                new() { Text = "好笑", Weight = 3 }, new() { Text = "开心", Weight = 3 },
                new() { Text = "高兴", Weight = 3 }, new() { Text = "太好了", Weight = 4 },
                new() { Text = "恭喜", Weight = 4 }, new() { Text = "棒", Weight = 2 },
                new() { Text = "不错", Weight = 1 }, new() { Text = "喜欢", Weight = 2 },
                new() { Text = "有趣", Weight = 2 }, new() { Text = "😄", Weight = 4 },
                new() { Text = "😊", Weight = 3 }, new() { Text = "🎉", Weight = 4 },
                new() { Text = "haha", Weight = 3 }, new() { Text = "great", Weight = 2 },
                new() { Text = "awesome", Weight = 3 }, new() { Text = "glad", Weight = 2 },
            ],
            [PetEmotions.Angry] =
            [
                new() { Text = "生气", Weight = 4 }, new() { Text = "气死", Weight = 5 },
                new() { Text = "可恶", Weight = 4 }, new() { Text = "讨厌", Weight = 3 },
                new() { Text = "烦", Weight = 2 }, new() { Text = "不能接受", Weight = 3 },
                new() { Text = "过分", Weight = 3 }, new() { Text = "哼", Weight = 3 },
                new() { Text = "😠", Weight = 4 }, new() { Text = "😡", Weight = 5 },
                new() { Text = "angry", Weight = 4 }, new() { Text = "annoying", Weight = 3 },
            ],
            [PetEmotions.Cry] =
            [
                new() { Text = "难过", Weight = 4 }, new() { Text = "伤心", Weight = 4 },
                new() { Text = "哭", Weight = 4 }, new() { Text = "呜呜", Weight = 4 },
                new() { Text = "抱歉", Weight = 2 }, new() { Text = "遗憾", Weight = 3 },
                new() { Text = "可惜", Weight = 2 }, new() { Text = "不开心", Weight = 3 },
                new() { Text = "失败", Weight = 2 }, new() { Text = "😢", Weight = 4 },
                new() { Text = "😭", Weight = 5 }, new() { Text = "sorry", Weight = 2 },
                new() { Text = "sad", Weight = 4 }, new() { Text = "unfortunately", Weight = 2 },
            ],
            [PetEmotions.Shy] =
            [
                new() { Text = "害羞", Weight = 4 }, new() { Text = "不好意思", Weight = 3 },
                new() { Text = "脸红", Weight = 4 }, new() { Text = "过奖", Weight = 3 },
                new() { Text = "哪里哪里", Weight = 3 }, new() { Text = "被夸", Weight = 3 },
                new() { Text = "讨厌啦", Weight = 4 }, new() { Text = "///", Weight = 3 },
                new() { Text = "😳", Weight = 4 }, new() { Text = "☺️", Weight = 3 },
                new() { Text = "blush", Weight = 4 }, new() { Text = "flattered", Weight = 3 },
            ],
        },
    };
}

/// <summary>
/// How one abstract action resolves for one model. <c>Kind</c> is <c>auto</c> (match by name), <c>motion</c>
/// (an explicit group and index), or <c>none</c> (skip this action entirely).
/// </summary>
public sealed record PetMotionRef
{
    public string Kind { get; init; } = "auto";

    public string? Group { get; init; }

    public int? No { get; init; }
}

/// <summary>How one expression resolves: <c>auto</c> by name, an explicit <c>.exp3</c> name, or <c>none</c>.</summary>
public sealed record PetExpressionRef
{
    public string Kind { get; init; } = "auto";

    public string? Name { get; init; }
}

/// <summary>One offset key in seconds, relative to the action's start.</summary>
public sealed record PetTrackKey
{
    public double At { get; init; }
    public double Value { get; init; }
}

/// <summary>A procedural offset track targeting an actual model parameter ID.</summary>
public sealed record PetExtraTrack
{
    public string Id { get; init; } = string.Empty;
    public IReadOnlyList<PetTrackKey> Keys { get; init; } = [];
}

/// <summary>Per-action multiplier and additional parameter tracks.</summary>
public sealed record PetProceduralAction
{
    public double? Amplitude { get; init; }
    public IReadOnlyList<PetExtraTrack> ExtraTracks { get; init; } = [];
}

/// <summary>
/// A model's mapping table and its click geometry. The proportions are only used for models without
/// <c>HitAreas</c>: a fraction of the model's bounding box, measured from the top.
/// </summary>
public sealed record PetModelProfile
{
    /// <summary>Action key → mapping. Keys are the runtime's abstract actions (<c>blink</c>, <c>greet</c>, …).</summary>
    public IReadOnlyDictionary<string, PetMotionRef> Motions { get; init; } =
        new Dictionary<string, PetMotionRef>(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, PetExpressionRef> Expressions { get; init; } =
        new Dictionary<string, PetExpressionRef>(StringComparer.Ordinal);

    /// <summary>Standard Cubism channel ID to actual model parameter ID.</summary>
    public IReadOnlyDictionary<string, string> Parameters { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Multiplier for procedural offsets; 1 preserves the built-in amplitude.</summary>
    public double Amplitude { get; init; } = 1;

    public IReadOnlyDictionary<string, PetProceduralAction> Procedural { get; init; } =
        new Dictionary<string, PetProceduralAction>(StringComparer.Ordinal);

    /// <summary>Below this fraction of the model height is the head.</summary>
    public double HeadBottom { get; init; } = 0.35;

    /// <summary>Below this fraction is the face; the rest is the body.</summary>
    public double FaceBottom { get; init; } = 0.55;

    /// <summary>Expression to return to; empty means "the model's own default".</summary>
    public string? DefaultExpression { get; init; }
}

/// <summary>
/// The desktop-pet section, stored in <c>runtime.json</c>. Whether the window is showing is the shell's own
/// preference (<c>pet-enabled</c>, beside <c>tray-enabled</c>) because the shell needs it before the API answers;
/// the page keeps <see cref="Enabled"/> in step with it through <c>set_pet_enabled</c>, like the tray.
/// </summary>
public sealed record PetSettings
{
    public bool Enabled { get; init; }

    public bool AlwaysOnTop { get; init; } = true;

    /// <summary>Let clicks pass through pixels the model does not cover.</summary>
    public bool ClickThroughTransparent { get; init; } = true;

    /// <summary>Play the sound a motion carries. Off by default: a desk pet that beeps is a surprise.</summary>
    public bool MotionSound { get; init; }

    /// <summary>0.5–2.0, multiplied by the model canvas size to size the window.</summary>
    public double Scale { get; init; } = 1.0;

    /// <summary>Imported model directory name; null means none is chosen.</summary>
    public string? ActiveModelId { get; init; }

    /// <summary>Idle action gap, in seconds. The runtime picks uniformly inside the range.</summary>
    public int IdleMinSeconds { get; init; } = 25;

    public int IdleMaxSeconds { get; init; } = 60;

    /// <summary>No interaction for this long sends the pet to sleep. 0 turns sleeping off.</summary>
    public int SleepAfterMinutes { get; init; } = 10;

    /// <summary><c>HH:mm</c> local times of the daily greeting; each fires once a day.</summary>
    public IReadOnlyList<string> GreetingTimes { get; init; } = ["09:00", "14:00"];

    public int EmotionCooldownSeconds { get; init; } = 8;

    public int ExpressionHoldSeconds { get; init; } = 6;

    /// <summary>Release speed, in physical pixels per second, that counts as "flung".</summary>
    public int FastDragPxPerSecond { get; init; } = 1500;

    public PetEmotionDictionary Emotions { get; init; } = PetEmotionDictionary.Default;

    /// <summary>Per-model overrides, keyed by imported model id.</summary>
    public IReadOnlyDictionary<string, PetModelProfile> Profiles { get; init; } =
        new Dictionary<string, PetModelProfile>(StringComparer.Ordinal);
}