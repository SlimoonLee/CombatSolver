using System.Text.Json;
using System.Text.Json.Nodes;
using CombatSolver;
using CombatSolver.Api;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Multiplayer.Replay;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace OfflineSearchHarness;

internal static class SavedCombatSetup
{
    internal static async Task<object> EnterAsync(HarnessOptions options)
    {
        SerializableRun save;
        object source;
        if (options.CombatReplayPath is { } replayPath)
        {
            byte[] bytes = File.ReadAllBytes(replayPath);
            PacketReader reader = new();
            reader.Reset(bytes);
            string version = reader.ReadString();
            string commit = reader.ReadString();
            uint hash = reader.ReadUInt();
            source = new { kind = "native_combat_replay", path = replayPath, version, commit,
                recordedModelIdHash = hash, currentModelIdHash = ModelIdSerializationCache.Hash };
            File.WriteAllText(Path.Combine(options.OutputDirectory, "replay-header.json"),
                JsonSerializer.Serialize(source, Program.Json));
            if (hash != ModelIdSerializationCache.Hash)
                throw new InvalidDataException($"Replay model ID table differs: recorded={hash:X8}, current={ModelIdSerializationCache.Hash:X8}.");
            reader.Reset(bytes);
            CombatReplay replay = reader.Read<CombatReplay>();
            save = replay.serializableRun;
        }
        else
        {
            string path = options.RunSavePath ?? throw new InvalidOperationException("Missing run save path.");
            byte[] bytes = File.ReadAllBytes(path);
            using JsonDocument document = JsonDocument.Parse(bytes);
            List<string> ignoredEmptyExtensions = [];
            ValidateExtensions(document.RootElement, "$", ignoredEmptyExtensions);
            save = JsonSerializer.Deserialize(bytes, JsonSerializationUtility.GetTypeInfo<SerializableRun>())
                ?? throw new InvalidDataException("Empty native run save.");
            source = new { kind = "native_run_save", path, extensionPolicy = "explicit_empty_mod_metadata",
                ignoredEmptyExtensions, environmentScope = "vanilla_models_without_original_mod_stack" };
        }
        int supportedSchema = SaveManager.Instance.GetLatestSchemaVersion<SerializableRun>();
        if (save.SchemaVersion != supportedSchema)
            throw new InvalidDataException($"Run save schema {save.SchemaVersion} differs from supported {supportedSchema}; offline migrations are disabled.");
        if (save.Players.Count != 1 || save.PreFinishedRoom != null || save.VisitedMapCoords.Count == 0)
            throw new NotSupportedException("Native combat import requires a singleplayer pre-room save with a visited coordinate and no pre-finished room.");

        byte[] expectedBytes = PreCombatRunSerialization.SerializeNormalized(save);
        RunState state = RunState.FromSerializable(save);
        await RunManager.Instance.SetUpSavedSingleplayer(state, save);
        RunManager.Instance.Launch();
        await RunManager.Instance.GenerateMap();
        byte[] actualBytes = PreCombatRunSerialization.SerializeNormalized(RunManager.Instance.ToSave(null));
        File.WriteAllBytes(Path.Combine(options.OutputDirectory, "run.restored.json"), actualBytes);
        JsonNode expected = JsonNode.Parse(expectedBytes)!;
        JsonNode actual = JsonNode.Parse(actualBytes)!;
        NormalizeEmptyRelicProps(expected);
        NormalizeEmptyRelicProps(actual);
        if (!JsonNode.DeepEquals(expected, actual))
        {
            File.WriteAllBytes(Path.Combine(options.OutputDirectory, "run.expected.json"), expectedBytes);
            throw new InvalidDataException("Native save round-trip changed normalized run state; inspect run.expected.json and run.restored.json.");
        }
        var player = LocalContext.GetMe(state) ?? throw new InvalidDataException("Saved local player missing.");
        object restored = new { source, normalizedRunUnchanged = true, supportedSchema,
            character = player.Character.Id.ToString(), seed = state.Rng.Seed, state.CurrentActIndex,
            state.AscensionLevel, hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp,
            deck = player.Deck.Cards.Select(card => card.ToSerializable()).ToArray(),
            relics = player.Relics.Select(relic => relic.ToSerializable()).ToArray(),
            visitedCoordinates = state.VisitedMapCoords.ToArray() };
        File.WriteAllText(Path.Combine(options.OutputDirectory, "save-import.json"),
            JsonSerializer.Serialize(restored, UnattendedTestFiles.JsonOptions));
        // This resumes the saved coordinate without recording it twice or rerolling a map.
        await RunManager.Instance.LoadIntoLatestMapCoord(null);
        return restored;
    }

    private static void NormalizeEmptyRelicProps(JsonNode root)
    {
        if (root["players"] is not JsonArray players) return;
        foreach (JsonObject player in players.OfType<JsonObject>())
            if (player["relics"] is JsonArray relics)
                foreach (JsonObject relic in relics.OfType<JsonObject>())
                    if (relic["props"] is JsonObject { Count: 0 }) relic.Remove("props");
    }

    internal static void ValidateExtensions(JsonElement value, string path, List<string> ignoredEmptyExtensions)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
                ValidateExtensions(item, $"{path}[{index++}]", ignoredEmptyExtensions);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string childPath = $"{path}.{property.Name}";
                if (property.Name.StartsWith("save_dict_", StringComparison.Ordinal))
                {
                    bool emptyModifiers = property.Name == "save_dict_List[BaseLib.Abstracts.CardModifier+ModifierSave]"
                        && property.Value.ValueKind == JsonValueKind.Object
                        && property.Value.EnumerateObject().Count() == 1
                        && property.Value.TryGetProperty("BaseLibCardModifiers", out JsonElement modifiers)
                        && modifiers.ValueKind == JsonValueKind.Array && modifiers.GetArrayLength() == 0;
                    if (!emptyModifiers && !IsEmptyLoadoutMetadata(property))
                        throw new NotSupportedException($"Unsupported saved mod extension at {childPath}.");
                    ignoredEmptyExtensions.Add(childPath);
                }
                else
                    ValidateExtensions(property.Value, childPath, ignoredEmptyExtensions);
            }
        }
    }

    private static bool IsEmptyLoadoutMetadata(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Object) return false;
        if (property.Name == "save_dict_Boolean")
            return property.Value.EnumerateObject().All(value => value.Name switch
            {
                "Loadout.panel_access.allow_guest_debug_console_v1" => value.Value.ValueKind == JsonValueKind.True,
                "Loadout.panel_access.allow_guests_v1" => value.Value.ValueKind == JsonValueKind.False,
                _ => false,
            });
        if (property.Name != "save_dict_String") return false;
        foreach (JsonProperty value in property.Value.EnumerateObject())
        {
            if (value.Value.ValueKind != JsonValueKind.String) return false;
            if (value.Name is "Loadout.custom_run.snapshot_v1" or "Loadout.map_editor.archive_v2")
            {
                if (value.Value.GetString() != "") return false;
            }
            else if (value.Name == "Loadout.content_bans.run_v1")
            {
                using JsonDocument bans = JsonDocument.Parse(value.Value.GetString()!);
                JsonElement root = bans.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 5
                    || !root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.GetInt32() != 1)
                    return false;
                foreach (string key in new[] { "cards", "relics", "potions", "events" })
                    if (!root.TryGetProperty(key, out JsonElement items) || items.ValueKind != JsonValueKind.Array
                        || items.GetArrayLength() != 0) return false;
            }
            else return false;
        }
        return true;
    }
}
