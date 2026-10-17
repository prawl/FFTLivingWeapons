using System;

namespace LivingWeapon;

/// <summary>
/// The landmarks, fill probes, stall handlers and texts of <see cref="TableDoors"/>, split out of
/// TableDoors.cs so that file keeps the lifecycle. Same partial class.
/// </summary>
internal sealed partial class TableDoors
{
    private const string AbilityWaiting = "waiting for the game to load its ability data";
    private const string ItemWaiting = "waiting for the game to load its item data";
    private const string AbilityNotLoaded = "ability data not loaded yet";
    private const string ItemNotLoaded = "item data not loaded yet";
    private const string DrillDetail = "drill: " + DrillTrigger.TableFlagName;
    private static readonly int[] MarkerRows = { 188, 189, 190 };
    private const int FillRow = 189;
    private const int FillFrom = 4;
    private const int FillLength = 14;
    private const int InflictByteIndex = ProvokePolicy.ActionInflictOffset - FillFrom;

    private bool Read(long addr, int len, out byte[] buf) => _mem.TryReadBytes(addr, len, out buf);

    private GuardLandmark Site(string name, long addr, byte[] bytes) => FingerprintGuard.ByteSignature(Read, addr, bytes, name);

    private GuardLandmark[] Terrain() => new[]
    {
        Site("terrain-veto-test-site", TableSignatures.TerrainVetoTestSite, TableSignatures.TerrainVetoTestBytes),
        Site("terrain-inspect-read-site", TableSignatures.TerrainInspectReadSite, TableSignatures.TerrainInspectReadBytes),
    };

    private GuardLandmark[] Palette() => new[]
    {
        PaletteDrill(Site("palette-clear-site", TableSignatures.PaletteClearSite, TableSignatures.PaletteClearBytes)),
        Site("palette-bank-b-site", TableSignatures.PaletteBankBSite, TableSignatures.PaletteBankBBytes),
    };

    private GuardLandmark[] Poach() => new[]
    {
        Site("poach-store-site-a", TableSignatures.PoachStoreSiteA, TableSignatures.PoachStoreBytesA),
        Site("poach-store-site-b", TableSignatures.PoachStoreSiteB, TableSignatures.PoachStoreBytesB),
    };

    private GuardLandmark[] ProvokeLandmarks() => new[]
    {
        Site("inflict-table-site", TableSignatures.InflictTableSite, TableSignatures.InflictTableBytes),
        FingerprintGuard.Custom("ability-row-markers", ProbeRowMarkers),
        LandmarkStall.Watch(FingerprintGuard.Custom("ability-row-fill", ProbeAbilityFill), LaunchGuard.DoorWaitSteps,
            (_, _) => NoteStall(ref _abilityStallNoted, "ability data",
                "The ability data check has not seen the game's ability table fill in for about a minute after a save loaded; provoke stays WAITING. It will keep trying."),
            _ => _rosterOpen()),
    };

    private GuardLandmark[] ItemLandmarks() => new[]
    {
        Site("item-stats-site", TableSignatures.ItemStatsSite, TableSignatures.ItemStatsBytes),
        LandmarkStall.Watch(FingerprintGuard.Custom("item-stats-fill", ProbeItemFill), LaunchGuard.DoorWaitSteps,
            (_, _) => NoteStall(ref _itemStallNoted, "item data",
                "The item data check still reads the game's original item numbers about a minute after a save loaded; gun growth stays WAITING. If the item rebalance is not loading either, check that the FFTIVC mod loader is enabled."),
            _ => _rosterOpen()),
    };

    private void NoteStall(ref bool noted, string door, string text)
    {
        if (noted) return;
        noted = true;
        _recorder?.Invoke("guard", $"stalled ({door} check unreadable for about a minute)");
        ModLogger.Event(LogVerb.Startup, text);
    }

    // The mismatch is forced on healthy bytes and the expectation is never perturbed, so a diag always prints real evidence.
    private GuardLandmark PaletteDrill(GuardLandmark inner) => new(inner.Name, () =>
    {
        var reading = inner.Probe();
        if (!_forceMismatch || reading.Verdict == LandmarkVerdict.Unreadable) return reading;
        if (reading.Verdict == LandmarkVerdict.Mismatch)
            return new LandmarkReading(LandmarkVerdict.Mismatch, reading.Detail + "; " + DrillDetail);
        return new LandmarkReading(LandmarkVerdict.Mismatch, DrillDetail);
    });

    private LandmarkReading ProbeRowMarkers()
    {
        var marker = TableSignatures.ActionRowMarker;
        string? bad = null;
        foreach (int row in MarkerRows)
        {
            if (!Read(RowAddr(row), marker.Length, out var buf) || buf.Length < marker.Length) return LandmarkVerdict.Unreadable;
            for (int i = 0; i < marker.Length; i++)
            {
                if (buf[i] == marker[i]) continue;
                string one = $"row {row} expected {BitConverter.ToString(marker)}, observed {BitConverter.ToString(buf, 0, marker.Length)}";
                bad = bad is null ? one : bad + "; " + one;
                break;
            }
        }
        return bad is null ? LandmarkVerdict.Match : new LandmarkReading(LandmarkVerdict.Mismatch, bad);
    }

    // The InflictStatus byte is masked because Provoke owns it, so the probe never reads the mod's own write.
    private LandmarkReading ProbeAbilityFill()
    {
        if (!Read(RowAddr(FillRow) + FillFrom, FillLength, out var buf) || buf.Length < FillLength) return LandmarkVerdict.Unreadable;
        for (int i = 0; i < FillLength; i++)
            if (i != InflictByteIndex && buf[i] != 0) return LandmarkVerdict.Match;
        return new LandmarkReading(LandmarkVerdict.Unreadable, AbilityNotLoaded);
    }

    // A row equal to its pinned vanilla bytes is the pre init table. Anything else, a partial fill or a rebalance mod included, is live.
    private LandmarkReading ProbeItemFill()
    {
        bool anyLoaded = false;
        for (int i = 0; i < TableSignatures.PinnedItemIds.Length; i++)
        {
            var vanilla = TableSignatures.PinnedItemVanillaRows[i];
            long addr = Offsets.ItemStatsBase + (long)TableSignatures.PinnedItemIds[i] * Offsets.ItemStatsStride;
            if (!Read(addr, vanilla.Length, out var buf) || buf.Length < vanilla.Length) return LandmarkVerdict.Unreadable;
            for (int j = 0; j < vanilla.Length; j++)
                if (buf[j] != vanilla[j]) { anyLoaded = true; break; }
        }
        return anyLoaded ? LandmarkVerdict.Match : new LandmarkReading(LandmarkVerdict.Unreadable, ItemNotLoaded);
    }

    private static long RowAddr(int row) => Offsets.LiveActionTable + (long)row * ProvokePolicy.ActionStride;

    private static string TerrainStandDownText(string diag)
        => $"The terrain data check failed ({diag}). The Sunderer's Bulwark is off this session so the mod never writes a wall into memory it does not understand; everything else keeps working.";

    private static string PaletteStandDownText(string diag)
        => $"The weapon palette check failed ({diag}). Weapon colors are off this session; everything else keeps working.";

    private static string PoachStandDownText(string diag)
        => $"The Poacher's Den store check failed ({diag}). Living Poach is off this session so a carcass is never written into the wrong place in your save; everything else keeps working.";

    private static string ProvokeStandDownText(string diag)
        => $"The ability data check failed ({diag}). Provoke is off this session so its effect is never pointed at data this mod does not recognize; everything else keeps working.";

    private static string ItemStandDownText(string diag)
        => $"The item data check failed ({diag}). The six guns' growth is off this session; everything else keeps working.";
}
