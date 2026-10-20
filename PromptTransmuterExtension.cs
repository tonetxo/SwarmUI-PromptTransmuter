using Newtonsoft.Json.Linq;
using SwarmUI.Accounts;
using SwarmUI.Core;
using SwarmUI.Text2Image;
using SwarmUI.Utils;
using SwarmUI.WebAPI;
using System.Text;
using System.Text.RegularExpressions;

namespace Tonetxo.PromptTransmuter;

/// <summary>Extension that adds a deterministic prompt mutator / "Evolve" tool to the Generate tab.</summary>
public class PromptTransmuterExtension : Extension
{
    /// <summary>Permission to use the Prompt Transmuter tool and API route.</summary>
    public static PermInfo PermUseTransmuter = Permissions.Register(new(
        "prompttransmuter_use",
        "[Prompt Transmuter] Use Prompt Transmuter",
        "Allows the user to mutate/evolve prompts with the Prompt Transmuter tool.",
        PermissionDefault.USER,
        Permissions.GroupUser
    ));

    /// <summary>Auto-transmute mode; its presence in the input is the group activation (the group only sends it when enabled).</summary>
    private static T2IRegisteredParam<string> AutoTransmuteMode;

    /// <summary>Auto-transmute probability percent of tokens to change.</summary>
    private static T2IRegisteredParam<int> AutoTransmuteStrength;

    /// <summary>Auto-transmute seed, -1 for random.</summary>
    private static T2IRegisteredParam<int> AutoTransmuteSeed;

    /// <summary>The valid transmutation modes.</summary>
    private static readonly string[] ValidModes = ["words", "internal", "synonyms"];

    public override void OnPreInit()
    {
        Version = "1.0.0";
        ExtensionAuthor = "Tonetxo";
        Description = "Adds a Prompt Transmuter / Evolve tool that rewrites prompts with cinematic vocabulary, internal word reuse, or synonyms.";
    }

    public override void OnInit()
    {
        ScriptFiles.Add("Assets/prompt_transmuter.js");
        StyleSheetFiles.Add("Assets/prompt_transmuter.css");
        API.RegisterAPICall(TransmutePrompt, false, PermUseTransmuter);
        RegisterAutoTransmuteParameters();
        Logs.Info("Prompt Transmuter extension loaded.");
    }

    /// <summary>Intentionally empty: keeps the metadata set in <see cref="OnPreInit"/> instead of the git-derived defaults, as this repository has no HTTPS remote.</summary>
    public override void PopulateMetadata()
    {
    }

    /// <summary>API route: returns 'count' transmuted variants of the prompt.</summary>
    public static Task<JObject> TransmutePrompt(Session session, string prompt, string mode, int strength, int count, int seed)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Prompt is empty" });
        }
        if (count < 1 || count > 16)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Variant count must be between 1 and 16" });
        }
        if (strength < 0 || strength > 100)
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = "Strength must be between 0 and 100" });
        }
        if (!ValidModes.Contains(mode))
        {
            return Task.FromResult(new JObject { ["success"] = false, ["error"] = $"Invalid mode '{mode}'. Valid modes: words, internal, synonyms" });
        }
        int baseSeed = HashCombine(seed, StableHash(mode), StableHash(prompt.Trim()));
        string[] variants = new string[count];
        int totalReplaced = 0;
        HashSet<string> matchedWords = [];
        for (int i = 0; i < count; i++)
        {
            (string text, int replaced, HashSet<string> matched) = Transmute(prompt, mode, strength, baseSeed + i);
            variants[i] = $"--- Variant {i + 1} ---\n{text}";
            totalReplaced += replaced;
            matchedWords.UnionWith(matched);
        }
        return Task.FromResult(new JObject
        {
            ["success"] = true,
            ["text"] = string.Join("\n\n", variants),
            ["variants"] = JArray.FromObject(variants),
            ["matched_words"] = JArray.FromObject(matchedWords.OrderBy(w => w)),
            ["replaced_count"] = totalReplaced,
            ["mode"] = mode,
            ["strength"] = strength
        });
    }

    /// <summary>Combines seed components into one seed.</summary>
    private static int HashCombine(int a, int b, int c)
    {
        int hash = 17;
        hash = hash * 31 + a;
        hash = hash * 31 + b;
        hash = hash * 31 + c;
        return hash;
    }

    /// <summary>FNV-1a hash of the UTF-8 text. Unlike <see cref="string.GetHashCode()"/> (randomized per process), it is stable across restarts, so a seed always reproduces the same variants.</summary>
    private static int StableHash(string text)
    {
        uint hash = 2166136261;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            hash = (hash ^ b) * 16777619;
        }
        return unchecked((int)hash);
    }

    /// <summary>Matches a plain word token.</summary>
    private static readonly Regex WordRegex = new(@"[a-zA-Z]+", RegexOptions.Compiled);

    /// <summary>
    /// Spans that must never be transmuted: Swarm prompt syntax (&lt;segment:...&gt;, &lt;region:...&gt;, &lt;base&gt;, ...)
    /// and the internal \0 markers PreparsePromptLikes leaves behind (embeddings, trigger phrases, presets).
    /// </summary>
    private static readonly Regex ProtectedRegex = new("\0swarmembed:.*?\0end|\0[a-zA-Z]+:?|<[^<>]*>", RegexOptions.Compiled | RegexOptions.Singleline);

    /// <summary>Collapses runs of spaces/tabs (never newlines, so multi-line prompts keep their structure).</summary>
    private static readonly Regex SpaceRunRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    /// <summary>Applies one transmutation pass and returns the text, the number of replaced tokens, and the matched source words.</summary>
    private static (string Text, int Replaced, HashSet<string> Matched) Transmute(string prompt, string mode, int strength, int seed)
    {
        Random rng = new(seed);
        HashSet<string> matched = [];
        int replaced = 0;
        string result = mode switch
        {
            "words" => ReplaceTokens(prompt, strength, rng, _ => EvolveVocab[rng.Next(EvolveVocab.Length)], null, ref replaced),
            "internal" => TransmuteInternal(prompt, strength, rng, matched, ref replaced),
            "synonyms" => TransmuteSynonyms(prompt, strength, rng, matched, ref replaced),
            _ => prompt
        };
        string cleaned = SpaceRunRegex.Replace(result, " ").Trim();
        return (cleaned, replaced, matched);
    }

    /// <summary>Returns the prompt text with protected spans removed, for collecting the prompt's own words.</summary>
    private static string UnprotectedText(string prompt)
    {
        return ProtectedRegex.Replace(prompt, " ");
    }

    /// <summary>Randomly replaces word tokens outside protected spans. A replacer returning null leaves the token unchanged and uncounted.</summary>
    private static string ReplaceTokens(string prompt, int strength, Random rng, Func<string, string> replacer, HashSet<string> matchedCollector, ref int replaced)
    {
        StringBuilder sb = new();
        int last = 0;
        foreach (Match protectedSpan in ProtectedRegex.Matches(prompt))
        {
            sb.Append(ReplaceTokensInPlainText(prompt[last..protectedSpan.Index], strength, rng, replacer, matchedCollector, ref replaced));
            sb.Append(protectedSpan.Value);
            last = protectedSpan.Index + protectedSpan.Length;
        }
        sb.Append(ReplaceTokensInPlainText(prompt[last..], strength, rng, replacer, matchedCollector, ref replaced));
        return sb.ToString();
    }

    /// <summary>Randomly replaces word tokens in text known to contain no protected spans.</summary>
    private static string ReplaceTokensInPlainText(string text, int strength, Random rng, Func<string, string> replacer, HashSet<string> matchedCollector, ref int replaced)
    {
        string[] parts = Regex.Split(text, @"(\b)");
        StringBuilder sb = new();
        foreach (string p in parts)
        {
            if (!WordRegex.IsMatch(p) || rng.NextDouble() * 100 >= strength)
            {
                sb.Append(p);
                continue;
            }
            string replacement = replacer(p);
            if (replacement is null)
            {
                sb.Append(p);
                continue;
            }
            replaced++;
            matchedCollector?.Add(p.ToLowerInvariant());
            if (p.Length > 0 && char.IsUpper(p[0]) && replacement.Length > 0)
            {
                sb.Append(char.ToUpperInvariant(replacement[0]));
                sb.Append(replacement[1..]);
            }
            else
            {
                sb.Append(replacement);
            }
        }
        return sb.ToString();
    }

    /// <summary>Replaces words with other words from the same prompt.</summary>
    private static string TransmuteInternal(string prompt, int strength, Random rng, HashSet<string> matched, ref int replaced)
    {
        List<string> rawWords = [.. WordRegex.Matches(UnprotectedText(prompt)).Select(m => m.Value)];
        if (rawWords.Count < 2)
        {
            return prompt;
        }
        return ReplaceTokens(prompt, strength, rng, token => rawWords[rng.Next(rawWords.Count)], matched, ref replaced);
    }

    /// <summary>Replaces words that have a known synonym.</summary>
    private static string TransmuteSynonyms(string prompt, int strength, Random rng, HashSet<string> matched, ref int replaced)
    {
        return ReplaceTokens(prompt, strength, rng, token =>
        {
            if (EvolveSynonyms.TryGetValue(token.ToLowerInvariant(), out string[] syns))
            {
                return syns[rng.Next(syns.Length)];
            }
            return null;
        }, matched, ref replaced);
    }

    /// <summary>Registers the 'Prompt Transmuter Auto' group and the handler that rewrites the prompt before generation.</summary>
    private static void RegisterAutoTransmuteParameters()
    {
        T2IParamGroup group = new(
            Name: "Prompt Transmuter Auto",
            Description: "Automatically transmute the prompt before generation.",
            Toggles: true,
            Open: false,
            OrderPriority: 8,
            IsAdvanced: true
        );

        AutoTransmuteMode = T2IParamTypes.Register<string>(new T2IParamType(
            Name: "Auto Transmute Mode",
            Description: "How the prompt is rewritten before generation while this group is enabled.",
            Default: "words",
            Group: group,
            OrderPriority: 1,
            GetValues: _ => [.. ValidModes],
            // The saved prompt is already transmuted: reusing this would transmute it a second time.
            Nonreusable: true
        ));

        AutoTransmuteStrength = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Auto Transmute Strength",
            Description: "Probability percent of tokens to change when auto-transmuting.",
            Default: "10",
            Min: 0, Max: 100, Step: 1,
            Group: group,
            OrderPriority: 2,
            ViewType: ParamViewType.SLIDER
        ));

        AutoTransmuteSeed = T2IParamTypes.Register<int>(new T2IParamType(
            Name: "Auto Transmute Seed",
            Description: "Seed for deterministic auto-transmutation. Use -1 for random.",
            Default: "-1",
            Min: -1, Max: int.MaxValue, Step: 1,
            Group: group,
            OrderPriority: 3,
            ViewType: ParamViewType.SEED
        ));

        T2IParamInput.LateSpecialParameterHandlers.Add(input =>
        {
            // The group only sends its params when enabled, so the mode's presence is the activation.
            if (!input.TryGet(AutoTransmuteMode, out string mode) || !ValidModes.Contains(mode))
            {
                return;
            }
            string prompt = input.Get(T2IParamTypes.Prompt, "");
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return;
            }
            int strength = input.Get(AutoTransmuteStrength, 10);
            int seed = input.Get(AutoTransmuteSeed, -1);
            if (seed == -1)
            {
                seed = Random.Shared.Next();
            }
            int variantSeed = HashCombine(seed, StableHash(mode), StableHash(prompt.Trim()));
            input.Set(T2IParamTypes.Prompt, Transmute(prompt, mode, strength, variantSeed).Text);
        });
    }

    /// <summary>Cinematic vocabulary used by the "words" mode.</summary>
    private static readonly string[] EvolveVocab =
    [
        "cinematic", "dramatic", "atmospheric", "moody", "ethereal", "surreal",
        "hyperrealistic", "photorealistic", "volumetric", "noir", "neon", "golden",
        "misty", "stormy", "serene", "tense", "epic", "intimate", "melancholic",
        "euphoric", "ominous", "dreamlike", "futuristic", "rustic", "decayed",
        "luxurious", "desolate", "lush", "intricate", "minimalist", "dynamic",
        "static", "fluid", "fragmented", "seamless", "chaotic", "ordered", "warm",
        "cold", "vibrant", "muted", "wide shot", "close up", "extreme close up",
        "medium shot", "overhead", "low angle", "dutch angle", "tracking", "handheld",
        "static tripod", "golden hour", "blue hour", "midday", "night", "dusk", "dawn",
        "backlit", "rim light", "soft light", "hard light", "film grain", "lens flare",
        "bokeh", "motion blur", "sharp focus", "shallow depth of field", "deep focus",
        "anamorphic", "35mm", "16mm", "IMAX", "digital", "vintage", "celluloid",
        "orchestral", "electronic", "ambient", "silence", "distant", "nearby",
        "echoing", "muffled", "crisp", "slow motion", "time lapse", "real time",
        "long take", "quick cut", "montage"
    ];

    /// <summary>Synonym table used by the "synonyms" mode (lowercase keys).</summary>
    private static readonly Dictionary<string, string[]> EvolveSynonyms = new()
    {
        ["big"] = ["massive", "enormous", "colossal", "immense", "towering"],
        ["large"] = ["vast", "expansive", "substantial", "grandiose", "mammoth"],
        ["small"] = ["tiny", "minuscule", "petite", "compact", "diminutive"],
        ["tall"] = ["towering", "looming", "soaring", "elevated", "statuesque"],
        ["short"] = ["petite", "compact", "stout", "squat", "diminutive"],
        ["fat"] = ["plump", "rotund", "stout", "corpulent", "bulbous"],
        ["thin"] = ["slender", "slim", "svelte", "lithe", "willowy"],
        ["strong"] = ["powerful", "mighty", "brawny", "sturdy", "forceful"],
        ["weak"] = ["frail", "feeble", "fragile", "delicate", "vulnerable"],
        ["fast"] = ["rapid", "swift", "quick", "accelerated", "hurried"],
        ["slow"] = ["leisurely", "gradual", "deliberate", "unhurried", "languid"],
        ["bright"] = ["luminous", "radiant", "dazzling", "vivid", "brilliant"],
        ["dim"] = ["faint", "murky", "subdued", "gloomy", "tenebrous"],
        ["colorful"] = ["vibrant", "multicolored", "chromatic", "kaleidoscopic", "psychedelic"],
        ["dull"] = ["muted", "drab", "flat", "lackluster", "subdued"],
        ["noisy"] = ["deafening", "thunderous", "cacophonous", "boisterous", "clamorous"],
        ["loud"] = ["deafening", "thunderous", "cacophonous", "boisterous", "clamorous"],
        ["quiet"] = ["silent", "hushed", "muffled", "subdued", "tranquil"],
        ["silent"] = ["quiet", "hushed", "muffled", "noiseless", "still"],
        ["hot"] = ["scorching", "blazing", "searing", "sweltering", "torrid"],
        ["warm"] = ["balmy", "mild", "sunlit", "cozy", "glowing"],
        ["cold"] = ["frigid", "freezing", "icy", "glacial", "wintry"],
        ["cool"] = ["crisp", "chilly", "brisk", "refreshing", "frosted"],
        ["wet"] = ["drenched", "soaked", "sodden", "glistening", "moist"],
        ["dry"] = ["arid", "parched", "desiccated", "dusty", "withered"],
        ["smooth"] = ["sleek", "silky", "glossy", "polished", "even"],
        ["rough"] = ["coarse", "rugged", "jagged", "gritty", "textured"],
        ["soft"] = ["plush", "cushioned", "velvety", "gentle", "tender"],
        ["hard"] = ["rigid", "stiff", "solid", "unyielding", "firm"],
        ["sharp"] = ["keen", "pointed", "jagged", "incisive", "crisp"],
        ["blunt"] = ["dull", "rounded", "soft", "obtuse", "flattened"],
        ["high"] = ["elevated", "soaring", "lofty", "towering", "suspended"],
        ["low"] = ["lowered", "sunken", "grounded", "crouched", "descending"],
        ["wide"] = ["broad", "expansive", "sprawling", "vast", "open"],
        ["narrow"] = ["slender", "thin", "tight", "constricted", "cramped"],
        ["deep"] = ["profound", "cavernous", "abyssal", "bottomless", "immersed"],
        ["shallow"] = ["superficial", "surface", "thin", "slight", "minimal"],
        ["heavy"] = ["ponderous", "weighty", "dense", "burdensome", "massive"],
        ["thick"] = ["dense", "chunky", "substantial", "bushy", "heavy"],
        ["long"] = ["extended", "lengthy", "elongated", "drawn-out", "lingering"],
        ["many"] = ["numerous", "countless", "abundant", "myriad", "multitudinous"],
        ["few"] = ["sparse", "scant", "meager", "limited", "rare"],
        ["full"] = ["complete", "brimming", "saturated", "abundant", "replete"],
        ["empty"] = ["vacant", "hollow", "desolate", "barren", "void"],
        ["closed"] = ["shut", "sealed", "locked", "enclosed", "covered"],
        ["near"] = ["close", "adjacent", "proximate", "nearby", "impending"],
        ["far"] = ["distant", "remote", "faraway", "removed", "looming"],
        ["above"] = ["overhead", "aloft", "elevated", "ascending", "suspended"],
        ["below"] = ["beneath", "under", "lower", "submerged", "descending"],
        ["front"] = ["foreground", "leading", "foremost", "ahead", "advance"],
        ["back"] = ["rear", "background", "retreating", "distant", "behind"],
        ["left"] = ["port", "sinister", "leftward", "counterclockwise", "western"],
        ["right"] = ["starboard", "dexter", "rightward", "clockwise", "eastern"],
        ["center"] = ["middle", "core", "heart", "focal", "central"],
        ["edge"] = ["rim", "border", "fringe", "periphery", "boundary"],
        ["top"] = ["summit", "peak", "apex", "uppermost", "zenith"],
        ["bottom"] = ["base", "foundation", "lowest", "nadir", "underbelly"],
        ["inside"] = ["interior", "within", "inner", "enclosed", "intimate"],
        ["outside"] = ["exterior", "outdoor", "open-air", "al fresco", "outer"],
        ["day"] = ["daylight", "daytime", "diurnal", "solar", "sunlit"],
        ["night"] = ["nocturnal", "midnight", "darkness", "evening", "moonlit"],
        ["morning"] = ["dawn", "sunrise", "daybreak", "matinal", "early"],
        ["evening"] = ["dusk", "twilight", "sunset", "gloaming", "vesper"],
        ["sunny"] = ["sunlit", "radiant", "gleaming", "bright", "luminous"],
        ["cloudy"] = ["overcast", "gloomy", "murky", "misty", "hazy"],
        ["rainy"] = ["stormy", "drenched", "pouring", "drizzling", "torrential"],
        ["windy"] = ["gusty", "blustery", "breezy", "howling", "whirling"],
        ["foggy"] = ["misty", "hazy", "murky", "opaque", "veiled"],
        ["snowy"] = ["wintry", "frosty", "white", "snow-covered", "icy"],
        ["green"] = ["verdant", "lush", "emerald", "jade", "sage"],
        ["blue"] = ["azure", "cerulean", "cobalt", "sapphire", "navy"],
        ["red"] = ["crimson", "scarlet", "ruby", "vermilion", "burgundy"],
        ["yellow"] = ["golden", "amber", "saffron", "lemon", "canary"],
        ["white"] = ["pale", "ivory", "alabaster", "snowy", "pearly"],
        ["black"] = ["jet", "ebony", "obsidian", "sable", "inky"],
        ["gray"] = ["ash", "slate", "silver", "gunmetal", "lead"],
        ["brown"] = ["tan", "sepia", "umber", "chestnut", "mahogany"],
        ["happy"] = ["joyful", "elated", "euphoric", "content", "radiant"],
        ["sad"] = ["melancholic", "somber", "mournful", "forlorn", "sorrowful"],
        ["angry"] = ["furious", "irate", "livid", "incensed", "wrathful"],
        ["excited"] = ["thrilled", "eager", "animated", "exhilarated", "frenzied"],
        ["bored"] = ["listless", "weary", "apathetic", "uninterested", "dull"],
        ["scared"] = ["terrified", "petrified", "horrified", "alarmed", "panicked"],
        ["brave"] = ["courageous", "fearless", "valiant", "bold", "heroic"],
        ["shy"] = ["timid", "bashful", "reserved", "demure", "hesitant"],
        ["proud"] = ["dignified", "stoic", "majestic", "regal", "imposing"],
        ["tired"] = ["weary", "exhausted", "fatigued", "spent", "drained"],
        ["surprised"] = ["astonished", "stunned", "startled", "aghast", "amazed"],
        ["curious"] = ["inquisitive", "questioning", "probing", "wondering", "intrigued"],
        ["beautiful"] = ["gorgeous", "stunning", "breathtaking", "exquisite", "radiant"],
        ["ugly"] = ["grotesque", "unsightly", "repulsive", "hideous", "monstrous"],
        ["old"] = ["ancient", "weathered", "aged", "antique", "timeworn"],
        ["new"] = ["pristine", "modern", "novel", "recent", "fresh"],
        ["young"] = ["youthful", "juvenile", "fresh-faced", "tender", "budding"],
        ["good"] = ["excellent", "superb", "magnificent", "stellar", "remarkable"],
        ["bad"] = ["dreadful", "abysmal", "atrocious", "deplorable", "lamentable"],
        ["real"] = ["authentic", "genuine", "lifelike", "tangible", "concrete"],
        ["fake"] = ["artificial", "synthetic", "counterfeit", "illusory", "false"],
        ["simple"] = ["minimal", "austere", "unadorned", "straightforward", "clean"],
        ["complex"] = ["intricate", "elaborate", "complicated", "ornate", "sophisticated"],
        ["rich"] = ["wealthy", "opulent", "luxurious", "lavish", "sumptuous"],
        ["poor"] = ["impoverished", "destitute", "shabby", "ramshackle", "threadbare"],
        ["safe"] = ["secure", "protected", "sheltered", "guarded", "stable"],
        ["dangerous"] = ["perilous", "hazardous", "treacherous", "menacing", "threatening"],
        ["clean"] = ["pristine", "spotless", "immaculate", "sterile", "polished"],
        ["dirty"] = ["grimy", "filthy", "soiled", "muddy", "squalid"],
        ["run"] = ["sprint", "dash", "race", "bolt", "charge"],
        ["walk"] = ["stride", "stroll", "saunter", "march", "amble"],
        ["jump"] = ["leap", "bound", "spring", "vault", "hop"],
        ["sit"] = ["perch", "settle", "recline", "slouch", "kneel"],
        ["stand"] = ["loom", "tower", "rise", "station", "pose"],
        ["look"] = ["gaze", "stare", "glance", "peer", "behold"],
        ["say"] = ["whisper", "shout", "declare", "mutter", "proclaim"],
        ["make"] = ["craft", "forge", "construct", "assemble", "create"],
        ["break"] = ["shatter", "fracture", "splinter", "rupture", "demolish"],
        ["give"] = ["bestow", "grant", "present", "hand", "deliver"],
        ["take"] = ["seize", "grab", "snatch", "claim", "capture"],
        ["begin"] = ["commence", "initiate", "launch", "embark", "inaugurate"],
        ["start"] = ["commence", "initiate", "launch", "embark", "inaugurate"],
        ["end"] = ["conclude", "terminate", "cease", "finalize", "culminate"],
        ["stop"] = ["halt", "cease", "pause", "freeze", "suspend"],
        ["come"] = ["arrive", "approach", "enter", "emerge", "appear"],
        ["go"] = ["depart", "leave", "exit", "vanish", "disappear"],
        ["know"] = ["understand", "comprehend", "grasp", "recognize", "perceive"],
        ["think"] = ["ponder", "contemplate", "reflect", "deliberate", "meditate"],
        ["want"] = ["desire", "crave", "yearn", "covet", "long for"],
        ["need"] = ["require", "demand", "necessitate", "warrant", "call for"],
        ["feel"] = ["sense", "perceive", "experience", "detect", "intuit"],
        ["see"] = ["observe", "witness", "behold", "discern", "sight"],
        ["hear"] = ["perceive", "detect", "listen", "catch", "make out"],
        ["kill"] = ["slay", "destroy", "annihilate", "vanquish", "eliminate"],
        ["fight"] = ["battle", "combat", "clash", "brawl", "duel"],
        ["hold"] = ["grasp", "clutch", "grip", "clasp", "support"],
        ["release"] = ["unleash", "liberate", "free", "loose", "discharge"],
        ["push"] = ["shove", "thrust", "propel", "drive", "press"],
        ["pull"] = ["tug", "drag", "haul", "yank", "draw"],
        ["throw"] = ["hurl", "toss", "fling", "cast", "pitch"],
        ["catch"] = ["snatch", "grab", "seize", "intercept", "capture"],
        ["open"] = ["unfold", "reveal", "uncover", "part", "spread"],
        ["close"] = ["shut", "seal", "fold", "converge", "clamp"],
        ["turn"] = ["rotate", "pivot", "swivel", "twist", "veer"],
        ["move"] = ["shift", "stir", "glide", "sweep", "advance"],
        ["wait"] = ["linger", "loiter", "pause", "hesitate", "remain"],
        ["follow"] = ["pursue", "trail", "chase", "shadow", "track"],
        ["lead"] = ["guide", "usher", "direct", "command", "pilot"],
        ["enter"] = ["penetrate", "step into", "cross into", "embark", "arrive"],
        ["leave"] = ["depart", "exit", "withdraw", "vacate", "retreat"],
        ["rise"] = ["ascend", "climb", "soar", "mount", "elevate"],
        ["fall"] = ["descend", "tumble", "plummet", "drop", "collapse"],
        ["grow"] = ["expand", "flourish", "bloom", "mature", "swell"],
        ["shrink"] = ["contract", "diminish", "dwindle", "compress", "recede"],
        ["burn"] = ["scorch", "sear", "char", "blaze", "immolate"],
        ["float"] = ["hover", "drift", "bob", "suspended", "levitate"],
        ["sink"] = ["submerge", "descend", "plunge", "drown", "settle"],
        ["explode"] = ["detonate", "erupt", "burst", "shatter", "combust"],
        ["shine"] = ["gleam", "glow", "radiate", "sparkle", "glimmer"],
        ["shadow"] = ["silhouette", "shade", "umbra", "penumbra", "gloom"],
        ["reflect"] = ["mirror", "echo", "repeat", "imitate", "resemble"],
        ["glow"] = ["radiate", "luminesce", "shine", "incandesce", "pulse"],
        ["smile"] = ["grin", "smirk", "beam", "smile warmly", "bare teeth"],
        ["laugh"] = ["chuckle", "guffaw", "cackle", "titter", "roar"],
        ["cry"] = ["weep", "sob", "wail", "whimper", "tear"],
        ["watch"] = ["observe", "monitor", "survey", "witness", "behold"],
        ["listen"] = ["hearken", "attend", "eavesdrop", "perceive", "strain to hear"],
        ["touch"] = ["caress", "graze", "brush", "tap", "fondle"],
        ["smell"] = ["inhale", "scent", "sniff", "reek", "perfume"],
        ["taste"] = ["savor", "sample", "lick", "relish", "devour"],
        ["eat"] = ["devour", "consume", "munch", "feast", "gobble"],
        ["drink"] = ["sip", "gulp", "quaff", "imbibe", "swallow"],
        ["cut"] = ["slice", "sever", "slash", "carve", "incise"],
        ["hit"] = ["strike", "smash", "bash", "slam", "pummel"],
        ["write"] = ["inscribe", "scribe", "pen", "etch", "record"],
        ["read"] = ["peruse", "scan", "study", "devour", "decipher"],
        ["draw"] = ["sketch", "render", "depict", "delineate", "trace"],
        ["paint"] = ["coat", "daub", "splash", "stain", "tint"],
        ["sing"] = ["croon", "chant", "serenade", "warble", "intone"],
        ["drive"] = ["pilot", "steer", "ride", "cruise", "navigate"],
        ["fly"] = ["soar", "pilot", "glide", "hover", "swoop"],
        ["swim"] = ["wade", "paddle", "float", "dive", "crawl"],
        ["climb"] = ["ascend", "scale", "clamber", "scramble", "mount"],
        ["hide"] = ["conceal", "camouflage", "cloak", "shroud", "secrete"],
        ["show"] = ["display", "reveal", "exhibit", "present", "unveil"],
        ["build"] = ["construct", "erect", "assemble", "forge", "raise"],
        ["create"] = ["craft", "forge", "shape", "formulate", "originate"],
        ["destroy"] = ["demolish", "annihilate", "obliterate", "ravage", "shatter"],
        ["protect"] = ["guard", "shield", "defend", "safeguard", "watch over"],
        ["attack"] = ["assault", "charge", "strike", "lunge", "besiege"],
        ["defend"] = ["protect", "guard", "shield", "fortify", "resist"],
        ["win"] = ["triumph", "prevail", "conquer", "dominate", "overcome"],
        ["lose"] = ["fail", "surrender", "yield", "fall", "be defeated"],
        ["change"] = ["transform", "transmute", "morph", "shift", "evolve"],
        ["stay"] = ["remain", "linger", "persist", "abide", "hold"],
        ["return"] = ["come back", "revert", "retreat", "reappear", "circle back"],
        ["escape"] = ["flee", "bolt", "evade", "break free", "slip away"],
        ["chase"] = ["pursue", "hunt", "trail", "shadow", "race after"],
        ["search"] = ["hunt", "scour", "probe", "ransack", "quest"],
        ["find"] = ["discover", "locate", "stumble upon", "detect", "uncover"],
        ["remember"] = ["recall", "recollect", "reminisce", "retrace", "evoke"],
        ["forget"] = ["overlook", "blank", "erase", "discard", "neglect"],
        ["believe"] = ["trust", "accept", "hold true", "conclude", "assume"],
        ["doubt"] = ["question", "suspect", "mistrust", "wonder", "hesitate"],
        ["trust"] = ["rely", "depend", "confide", "have faith", "entrust"],
        ["secret"] = ["mystery", "enigma", "riddle", "concealment", "hidden truth"],
        ["story"] = ["tale", "narrative", "account", "saga", "chronicle"],
        ["picture"] = ["image", "portrait", "depiction", "rendering", "scene"],
        ["fire"] = ["flame", "blaze", "inferno", "conflagration", "embers"],
        ["water"] = ["liquid", "aqua", "fluid", "torrent", "deluge"],
        ["earth"] = ["soil", "ground", "terrain", "land", "dust"],
        ["air"] = ["atmosphere", "breeze", "wind", "ether", "sky"],
        ["metal"] = ["steel", "iron", "bronze", "copper", "alloy"],
        ["wood"] = ["timber", "lumber", "plank", "log", "forest"],
        ["stone"] = ["rock", "boulder", "pebble", "slate", "granite"],
        ["glass"] = ["crystal", "pane", "shard", "transparent", "fragile"],
        ["paper"] = ["parchment", "vellum", "sheet", "document", "page"],
        ["cloth"] = ["fabric", "textile", "linen", "silk", "canvas"],
        ["hair"] = ["locks", "tresses", "mane", "strands", "curls"],
        ["hand"] = ["palm", "fist", "claw", "grasp", "digits"],
        ["foot"] = ["sole", "paw", "hoof", "tread", "step"],
        ["head"] = ["skull", "cranium", "brow", "crown", "visage"],
        ["face"] = ["visage", "countenance", "features", "expression", "mien"],
        ["body"] = ["form", "figure", "physique", "torso", "frame"],
        ["mind"] = ["consciousness", "thoughts", "intellect", "psyche", "reason"],
        ["soul"] = ["spirit", "essence", "psyche", "inner being", "life force"],
        ["ghost"] = ["specter", "phantom", "apparition", "wraith", "shade"],
        ["monster"] = ["beast", "creature", "abomination", "fiend", "horror"],
        ["angel"] = ["seraph", "celestial", "divine messenger", "guardian", "saint"],
        ["devil"] = ["demon", "imp", "fiend", "evil spirit", "tempter"],
        ["god"] = ["deity", "divinity", "celestial", "omnipotence", "providence"],
        ["king"] = ["monarch", "sovereign", "ruler", "lord", "emperor"],
        ["queen"] = ["monarch", "sovereign", "matriarch", "lady", "empress"],
        ["hero"] = ["champion", "protagonist", "savior", "knight", "valiant"],
        ["villain"] = ["antagonist", "scoundrel", "villain", "fiend", "malefactor"],
        ["warrior"] = ["fighter", "soldier", "combatant", "champion", "mercenary"],
        ["wizard"] = ["mage", "sorcerer", "warlock", "magician", "enchanter"],
        ["witch"] = ["sorceress", "enchantress", "hag", "crone", "spellcaster"],
        ["dragon"] = ["wyrm", "drake", "serpent", "beast", "titan"],
        ["bird"] = ["avian", "fowl", "winged creature", "songbird", "raptor"],
        ["mountain"] = ["peak", "summit", "ridge", "massif", "highland"],
        ["river"] = ["stream", "brook", "current", "waterway", "torrent"],
        ["ocean"] = ["sea", "marine", "abyss", "brine", "deep"],
        ["forest"] = ["woods", "woodland", "grove", "thicket", "jungle"],
        ["desert"] = ["wasteland", "dunes", "barrens", "arid expanse", "wilderness"],
        ["city"] = ["metropolis", "urban center", "town", "municipality", "sprawl"],
        ["village"] = ["hamlet", "settlement", "township", "rural community", "outpost"],
        ["house"] = ["dwelling", "abode", "residence", "home", "domicile"],
        ["room"] = ["chamber", "space", "compartment", "quarters", "hall"],
        ["window"] = ["pane", "opening", "aperture", "casement", "skylight"],
        ["road"] = ["pathway", "route", "avenue", "highway", "trail"],
        ["bridge"] = ["span", "crossing", "viaduct", "overpass", "arc"],
        ["tower"] = ["spire", "turret", "bastion", "column", "obelisk"],
        ["wall"] = ["barrier", "rampart", "partition", "façade", "fortification"],
        ["floor"] = ["ground", "surface", "deck", "pavement", "base"],
        ["ceiling"] = ["vault", "canopy", "overhead", "roof", "dome"],
        ["roof"] = ["canopy", "cover", "shelter", "shingle", "top"],
        ["table"] = ["surface", "counter", "desk", "stand", "board"],
        ["chair"] = ["seat", "stool", "throne", "bench", "saddle"],
        ["bed"] = ["cot", "bunk", "mattress", "sleeping place", "rest"],
        ["book"] = ["tome", "volume", "manuscript", "codex", "publication"],
        ["cup"] = ["goblet", "mug", "chalice", "tumbler", "vessel"],
        ["plate"] = ["dish", "platter", "saucer", "tray", "charger"],
        ["bowl"] = ["basin", "vessel", "dish", "container", "tureen"],
        ["bottle"] = ["flask", "vial", "decanter", "carafe", "container"],
        ["box"] = ["crate", "chest", "container", "carton", "casket"],
        ["bag"] = ["sack", "pouch", "satchel", "tote", "pack"],
        ["ring"] = ["band", "circle", "hoop", "loop", "band"],
        ["crown"] = ["diadem", "coronet", "tiara", "circlet", "laurel"],
        ["sword"] = ["blade", "weapon", "saber", "rapier", "broadsword"],
        ["shield"] = ["buckler", "aegis", "guard", "barrier", "defense"],
        ["bow"] = ["longbow", "crossbow", "arc", "curve", "weapon"],
        ["arrow"] = ["bolt", "dart", "shaft", "projectile", "missile"],
        ["gun"] = ["firearm", "weapon", "rifle", "pistol", "musket"],
        ["car"] = ["automobile", "vehicle", "motorcar", "sedan", "machine"],
        ["ship"] = ["vessel", "boat", "craft", "schooner", "galleon"],
        ["plane"] = ["aircraft", "airplane", "jet", "vessel", "flying machine"],
        ["train"] = ["locomotive", "railway", "carriage", "express", "freight"],
        ["bike"] = ["bicycle", "cycle", "two-wheeler", "motorcycle", "pedal"],
        ["camera"] = ["photographic device", "lens", "shutter", "film camera", "optical instrument"],
        ["phone"] = ["telephone", "mobile", "handset", "device", "communicator"],
        ["computer"] = ["machine", "device", "terminal", "workstation", "processor"],
        ["screen"] = ["display", "monitor", "panel", "interface", "viewing surface"],
        ["music"] = ["melody", "harmony", "rhythm", "tune", "composition"],
        ["dance"] = ["twirl", "sway", "prance", "spin", "leap"],
        ["art"] = ["fine art", "creation", "work", "masterpiece", "composition"],
        ["color"] = ["hue", "shade", "tint", "tone", "pigment"],
        ["shape"] = ["form", "figure", "outline", "silhouette", "contour"],
        ["line"] = ["stroke", "curve", "edge", "contour", "mark"],
        ["circle"] = ["ring", "loop", "orb", "round", "disk"],
        ["square"] = ["quadrate", "block", "rectangle", "box", "cube"],
        ["triangle"] = ["trigon", "wedge", "pyramid", "trio", "corner"],
        ["star"] = ["celestial body", "luminary", "stellar object", "heavenly body", "astral"],
        ["moon"] = ["lunar body", "satellite", "crescent", "orb", "night orb"],
        ["sun"] = ["solar orb", "daystar", "luminary", "sol", "glowing sphere"],
        ["cloud"] = ["nimbus", "cumulus", "vapor", "mist", "billow"],
        ["rain"] = ["downpour", "shower", "drizzle", "precipitation", "deluge"],
        ["snow"] = ["flurries", "blizzard", "powder", "frost", "whiteout"],
        ["wind"] = ["breeze", "gale", "zephyr", "squall", "current"],
        ["storm"] = ["tempest", "squall", "thunderstorm", "hurricane", "maelstrom"],
        ["thunder"] = ["rumble", "boom", "crash", "peal", "resonance"],
        ["lightning"] = ["bolt", "flash", "discharge", "arc", "streak"],
        ["fog"] = ["mist", "haze", "murk", "vapor", "opacity"],
        ["dust"] = ["particles", "grit", "powder", "debris", "film"],
        ["smoke"] = ["vapor", "fumes", "haze", "smolder", "plume"],
        ["steam"] = ["vapor", "mist", "condensation", "exhalation", "hiss"],
        ["bubble"] = ["globule", "orb", "sphere", "blister", "pocket"],
        ["drop"] = ["droplet", "bead", "globule", "tear", "pellet"],
        ["wave"] = ["swell", "surge", "crest", "breaker", "ripple"],
        ["flame"] = ["blaze", "fire", "inferno", "tongue of fire", "lick"],
        ["spark"] = ["ember", "glint", "speck", "flash", "ignition"],
        ["ash"] = ["cinder", "embers", "soot", "dust", "residue"],
        ["ice"] = ["frost", "crystal", "glacier", "sleet", "rime"],
        ["crystal"] = ["gem", "prism", "facet", "mineral", "quartz"],
        ["gold"] = ["aurum", "golden", "gilded", "metallic", "treasure"],
        ["silver"] = ["argent", "silvery", "metallic", "lustrous", "grey-white"],
        ["jewelry"] = ["adornment", "ornament", "bauble", "trinket", "finery"],
        ["clothes"] = ["garments", "attire", "apparel", "raiment", "costume"],
        ["shoes"] = ["footwear", "boots", "sandals", "slippers", "heels"],
        ["hat"] = ["cap", "headwear", "bonnet", "helm", "chapeau"],
        ["dress"] = ["gown", "frock", "robe", "garment", "costume"],
        ["coat"] = ["jacket", "cloak", "mantle", "overcoat", "wrap"],
        ["shirt"] = ["blouse", "top", "tunic", "garment", "clothing"],
        ["pants"] = ["trousers", "slacks", "breeches", "jeans", "bottoms"],
        ["skirt"] = ["kilt", "petticoat", "sarong", "dress", "hem"],
        ["armor"] = ["plate", "mail", "vest", "cuirass", "protection"],
        ["uniform"] = ["livery", "regalia", "attire", "costume", "garb"],
        ["mask"] = ["disguise", "veil", "visage", "facade", "concealment"],
        ["weapon"] = ["arm", "instrument of war", "blade", "firearm", "tool of combat"],
        ["tool"] = ["instrument", "implement", "device", "utensil", "apparatus"],
        ["machine"] = ["mechanism", "device", "apparatus", "engine", "contraption"],
        ["robot"] = ["automaton", "android", "mech", "droid", "cyborg"],
        ["alien"] = ["extraterrestrial", "being", "creature", "visitor", "unknown lifeform"],
        ["magic"] = ["sorcery", "wizardry", "enchantment", "thaumaturgy", "arcane power"],
        ["spell"] = ["incantation", "charm", "hex", "curse", "enchantment"],
        ["potion"] = ["elixir", "brew", "concoction", "tincture", "draught"],
        ["treasure"] = ["riches", "hoard", "wealth", "fortune", "valuables"],
        ["map"] = ["chart", "cartography", "plan", "diagram", "atlas"],
        ["key"] = ["passkey", "lock opener", "access token", "device", "solution"],
        ["door"] = ["portal", "entryway", "gate", "threshold", "hatch"],
        ["path"] = ["trail", "route", "way", "course", "passage"],
        ["journey"] = ["voyage", "expedition", "pilgrimage", "trek", "odyssey"],
        ["quest"] = ["mission", "errand", "adventure", "crusade", "undertaking"],
        ["battle"] = ["conflict", "skirmish", "clash", "engagement", "duel"],
        ["war"] = ["conflict", "campaign", "struggle", "hostilities", "siege"],
        ["peace"] = ["tranquility", "harmony", "serenity", "calm", "stillness"],
        ["love"] = ["adore", "cherish", "treasure", "revere", "idolize"],
        ["hate"] = ["despise", "loathe", "abhor", "detest", "execrate"],
        ["friend"] = ["companion", "ally", "confidant", "comrade", "partner"],
        ["enemy"] = ["foe", "adversary", "antagonist", "rival", "nemesis"],
        ["family"] = ["kin", "clan", "household", "lineage", "relatives"],
        ["child"] = ["youngster", "youth", "minor", "offspring", "progeny"],
        ["parent"] = ["mother", "father", "guardian", "progenitor", "caregiver"],
        ["woman"] = ["female", "lady", "maiden", "matron", "feminine figure"],
        ["man"] = ["male", "gentleman", "fellow", "masculine figure", "chap"],
        ["girl"] = ["young female", "maiden", "lass", "damsel", "juvenile"],
        ["boy"] = ["young male", "lad", "youth", "juvenile", "stripling"],
        ["people"] = ["crowd", "folk", "masses", "populace", "throng"],
        ["person"] = ["individual", "human", "figure", "being", "soul"],
        ["human"] = ["person", "mortal", "being", "individual", "figure"],
        ["animal"] = ["creature", "beast", "critter", "fauna", "wildlife"],
        ["dog"] = ["canine", "hound", "pup", "mutt", "terrier"],
        ["cat"] = ["feline", "kitten", "tabby", "tom", "puss"],
        ["horse"] = ["steed", "mare", "stallion", "gelding", "mount"],
        ["wolf"] = ["canine", "lupine", "predator", "howler", "pack hunter"],
        ["bear"] = ["bruin", "ursine", "grizzly", "predator", "hulk"],
        ["eagle"] = ["raptor", "bird of prey", "hawk", "falcon", "aerie"],
        ["snake"] = ["serpent", "reptile", "viper", "python", "cobra"],
        ["spider"] = ["arachnid", "tarantula", "web-weaver", "creepy-crawly", "eight-legged"],
        ["insect"] = ["bug", "arthropod", "creepy-crawly", "vermin", "pest"],
        ["fish"] = ["aquatic creature", "sea creature", "finned beast", "swimmer", "piscine"],
        ["shark"] = ["predator", "great white", "hammerhead", "marine hunter", "cartilaginous fish"],
        ["whale"] = ["leviathan", "cetacean", "marine mammal", "giant", "krill-eater"],
        ["butterfly"] = ["lepidopteran", "moth", "winged insect", "flutterer", "pollinator"],
        ["flower"] = ["bloom", "blossom", "floret", "bud", "posy"],
        ["rose"] = ["thorny bloom", "crimson flower", "symbol of love", "thorn", "bush"],
        ["tree"] = ["arbor", "timber", "wood", "sapling", "trunk"],
        ["leaf"] = ["frond", "foliage", "blade", "needle", "petal"],
        ["branch"] = ["bough", "limb", "twig", "shoot", "offshoot"],
        ["root"] = ["radicle", "taproot", "underground stem", "anchor", "foundation"],
        ["seed"] = ["germ", "kernel", "pip", "spore", "embryo"],
        ["fruit"] = ["produce", "berry", "pome", "citrus", "harvest"],
        ["apple"] = ["pome", "crimson fruit", "orchard fruit", "forbidden fruit", "malus"],
        ["sunflower"] = ["helianthus", "golden bloom", "turnsol", "tall flower", "compass plant"],
        ["grass"] = ["turf", "sward", "pasture", "lawn", "verdure"],
        ["field"] = ["meadow", "pasture", "plain", "expanse", "tract"],
        ["garden"] = ["grove", "arboretum", "hortus", "cultivated plot", "flowerbed"],
        ["park"] = ["gardens", "green space", "recreation area", "common", "grounds"],
        ["farm"] = ["homestead", "ranch", "plantation", "croft", "grange"],
        ["castle"] = ["fortress", "keep", "citadel", "stronghold", "palace"],
        ["palace"] = ["mansion", "estate", "chateau", "villa", "royal residence"],
        ["temple"] = ["shrine", "sanctuary", "church", "mosque", "tabernacle"],
        ["church"] = ["chapel", "cathedral", "basilica", "house of worship", "sanctuary"],
        ["cave"] = ["cavern", "grotto", "hollow", "den", "underground chamber"],
        ["ruins"] = ["remains", "relics", "wreckage", "debris", "vestiges"],
        ["grave"] = ["tomb", "burial site", "mausoleum", "sepulcher", "mound"],
        ["hell"] = ["inferno", "underworld", "abyss", "perdition", "netherworld"],
        ["heaven"] = ["paradise", "empyrean", "elysium", "celestial realm", "afterlife"],
        ["nightmare"] = ["bad dream", "horror", "terror", "incubus", "succubus"],
        ["time"] = ["tempus", "duration", "epoch", "era", "moment"],
        ["space"] = ["void", "expanse", "cosmos", "universe", "infinity"],
        ["future"] = ["tomorrow", "forthcoming", "impending", "prospective", "yet-to-come"],
        ["past"] = ["history", "yesteryear", "bygone", "antiquity", "former times"],
        ["present"] = ["now", "current", "moment", "today", "immediate"],
        ["life"] = ["existence", "being", "vitality", "animation", "living"],
        ["death"] = ["demise", "passing", "mortality", "expiration", "end"],
        ["birth"] = ["nativity", "genesis", "origin", "beginning", "emergence"],
        ["blood"] = ["vital fluid", "ichor", "lifeblood", "gore", "plasma"],
        ["bone"] = ["skeleton", "os", "remains", "calcium structure", "frame"],
        ["skin"] = ["flesh", "dermis", "complexion", "hide", "membrane"],
        ["eye"] = ["gaze", "iris", "pupil", "orb", "lookout"],
        ["tear"] = ["droplet", "teardrop", "cry", "lament", "sorrow"],
        ["breath"] = ["respiration", "exhalation", "inhalation", "gasp", "puff"],
        ["heart"] = ["core", "center", "essence", "soul", "spirit"],
        ["brain"] = ["mind", "intellect", "cerebrum", "gray matter", "reason"],
        ["voice"] = ["speech", "utterance", "call", "whisper", "proclamation"],
        ["song"] = ["ballad", "melody", "tune", "chant", "aria"],
        ["word"] = ["term", "utterance", "expression", "syllable", "name"],
        ["name"] = ["moniker", "designation", "title", "handle", "epithet"],
        ["number"] = ["digit", "figure", "numeral", "quantity", "count"],
        ["sound"] = ["noise", "tone", "resonance", "echo", "vibration"],
        ["darkness"] = ["blackness", "gloom", "obscurity", "shadow", "void"],
        ["light"] = ["luminous", "radiant", "brilliant", "gleaming", "ethereal"],
        ["energy"] = ["power", "force", "vigor", "dynamism", "vitality"],
        ["power"] = ["strength", "might", "force", "authority", "potency"],
        ["speed"] = ["velocity", "rapidity", "pace", "momentum", "haste"],
        ["strength"] = ["might", "power", "force", "vigor", "potency"],
        ["weakness"] = ["frailty", "vulnerability", "deficiency", "shortcoming", "failing"],
        ["beauty"] = ["loveliness", "allure", "grace", "elegance", "radiance"],
        ["truth"] = ["verity", "fact", "reality", "actuality", "honesty"],
        ["lie"] = ["deception", "falsity", "untruth", "fabrication", "fiction"],
        ["freedom"] = ["liberty", "autonomy", "independence", "release", "emancipation"],
        ["prison"] = ["jail", "dungeon", "cell", "cage", "captivity"],
        ["justice"] = ["fairness", "equity", "righteousness", "law", "judgment"],
        ["fear"] = ["dread", "terror", "trepidation", "apprehension", "alarm"],
        ["hope"] = ["wish", "aspire", "anticipate", "long", "expect"],
        ["joy"] = ["delight", "bliss", "euphoria", "glee", "rapture"],
        ["sorrow"] = ["grief", "anguish", "misery", "woe", "despair"],
        ["anger"] = ["rage", "fury", "wrath", "ire", "indignation"],
        ["calm"] = ["serenity", "tranquility", "peace", "stillness", "composure"],
        ["chaos"] = ["turmoil", "disorder", "confusion", "pandemonium", "maelstrom"],
        ["order"] = ["structure", "organization", "system", "arrangement", "harmony"],
        ["silence"] = ["quiet", "stillness", "hush", "muteness", "absence of sound"],
        ["noise"] = ["din", "racket", "clamor", "uproar", "commotion"],
        ["pain"] = ["agony", "anguish", "suffering", "distress", "torment"],
        ["pleasure"] = ["delight", "enjoyment", "gratification", "bliss", "satisfaction"],
        ["hunger"] = ["starvation", "famine", "appetite", "craving", "ravenousness"],
        ["thirst"] = ["parchedness", "dehydration", "craving", "longing", "dryness"],
        ["sleep"] = ["slumber", "doze", "rest", "repose", "nap"],
        ["wake"] = ["awaken", "arise", "rouse", "stir", "come to"],
        ["dream"] = ["imagine", "fantasize", "hallucinate", "vision", "reverie"]
    };
}
