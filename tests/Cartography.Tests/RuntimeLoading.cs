using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class Program
{
    private static void RuntimeLoading()
    {
        string root = Path.Combine(output, "runtime-" + Guid.NewGuid().ToString("N")), assets = Path.Combine(root, "StreamingAssets");
        RWCustom.Custom.Root = assets;
        BepInEx.Paths.ConfigPath = Path.Combine(root, "config");
        foreach (string code in new[] { "B5", "CC", "SU", "HI", "DS", "GW" })
        {
            string folder = Path.Combine(assets, "world", code.ToLowerInvariant()); Directory.CreateDirectory(folder);
            string roomName = code + "_A01";
            File.WriteAllText(Path.Combine(folder, "world_" + code.ToLowerInvariant() + ".txt"), "ROOMS\n" + roomName + " : DISCONNECTED\nEND ROOMS\n");
            File.WriteAllText(Path.Combine(folder, "map_" + code.ToLowerInvariant() + ".txt"), roomName + ": 0><0><20><0><0><\n");
            File.WriteAllText(Path.Combine(folder, "displayname.txt"), "区域全称 " + code);
            string[] room = Enumerable.Repeat("", 12).ToArray(); room[0] = roomName; room[1] = "2*2|-1|0"; room[11] = "0|1|0|1|";
            File.WriteAllLines(Path.Combine(folder, roomName.ToLowerInvariant() + ".txt"), room);
        }
        var session = new EditorSession { World = new World { name = "B5" } };
        void Finish()
        {
            Stopwatch timeout = Stopwatch.StartNew();
            do
            {
                CartographyRuntime.Process(session);
                if (!CartographyRuntime.LoadingSource) break;
                Thread.Sleep(5);
            } while (timeout.Elapsed.TotalSeconds < 20);
            Check(!CartographyRuntime.LoadingSource && !CartographyRuntime.SourcePicker.Failed, "Runtime source preparation completes: " + CartographyRuntime.SourcePicker.Status);
        }
        void Select(string region, bool refresh = false) => CartographyRuntime.Enqueue(new CartographyCommand { Kind = CartographyCommandKind.SelectRegion, LayerId = region, RefreshSource = refresh });
        CartographyRuntime.SetActive(true); Finish();
        Check(CartographyRuntime.Presentation.Document.Region == "B5" && CartographyRuntime.SourcePicker.Campaign == "Yellow", "First open uses the player's region and campaign, never CC/White defaults.");
        Check(CartographyRuntime.SourcePicker.Regions.Length == 6, "The selector is populated from available world files.");
        var first = CartographyRuntime.Presentation;
        var firstScene = first.Scene;
        CartographyRuntime.Enqueue(new CartographyCommand { Kind = CartographyCommandKind.Move, DocumentId = first.Identity,
            Revision = first.Revision, Ids = new[] { "room:B5_A01" }, X = 50 });
        CartographyRuntime.Process(session);
        var edited = CartographyRuntime.Presentation;
        Check(edited.Dirty && edited.Document.Items[0].X == first.Document.Items[0].X + 50, "The runtime records an unsaved author edit.");
        Select("CC"); Finish(); Select("B5"); Finish();
        Check(CartographyRuntime.Presentation.Document.Items[0].X == edited.Document.Items[0].X && CartographyRuntime.Presentation.Dirty, "Switching away/back retains unsaved edits.");
        Check(ReferenceEquals(CartographyRuntime.Presentation.Scene, edited.Scene), "Warm region switches retain prepared raster scenes.");
        Check(CartographyRuntime.HistoryFor(session).Undo(session), "Region switching retains the real undo history.");
        Check(CartographyRuntime.Presentation.Document.Items[0].X == first.Document.Items[0].X, "Undo after a region switch restores author position.");

        Select("CC"); CartographyRuntime.Process(session); // Queue first load.
        Select("SU"); Select("HI"); Finish();
        Check(CartographyRuntime.Presentation.Document.Region == "HI", "Rapid switching publishes only the final requested region.");
        Select("missing"); CartographyRuntime.Process(session);
        Check(CartographyRuntime.SourcePicker.Failed && CartographyRuntime.Presentation.Document.Region == "HI", "A failed load leaves the last valid workspace visible.");
        Check(DryCycle.Plugin.Logger.Errors.Any(error => error.Contains("FileNotFoundException")), "The original load exception is logged.");
        CartographyRuntime.SetActive(false); CartographyRuntime.SetActive(true); Finish();
        Check(CartographyRuntime.Presentation.Document.Region == "B5", "Reopening returns to the player's region after browsing elsewhere.");
        foreach (string code in new[] { "CC", "SU", "HI", "DS", "GW" }) { Select(code); Finish(); }
        Select("B5"); Finish();
        Check(CartographyRuntime.HistoryFor(session).Redo(session), "Evicting derived previews retains the author's redo history.");
        Check(CartographyRuntime.Presentation.Document.Items[0].X == edited.Document.Items[0].X, "Rebuilt source preserves authored positions after eviction.");
        Check(CartographyRuntime.SaveActive(session), "The retained author document saves through the existing persistence boundary.");
        Select("B5", true); CartographyRuntime.Process(session);
        Check(CartographyRuntime.SaveActive(session), "Saving while a refresh is pending succeeds.");
        Finish();
        Check(!CartographyRuntime.Presentation.Dirty, "Background refresh does not overwrite the save baseline.");
        session.World = new World { name = "CC" }; Finish();
        Check(CartographyRuntime.Presentation.Document.Region == "CC", "A changed player world follows the new current region.");
        CartographyRuntime.ResetView();
    }
}
