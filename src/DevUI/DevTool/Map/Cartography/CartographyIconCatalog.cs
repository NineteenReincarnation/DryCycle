using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

// Semantic names from placed-object data are not Futile atlas element names.
internal static class CartographyIconCatalog
{
    internal static readonly Dictionary<string, (string Sprite, uint Color)> Objects = new(StringComparer.Ordinal)
    {
        ["VultureGrub"] = ("Kill_VultureGrub", 0xFFD4CA6F), ["Hazer"] = ("Kill_Hazer", 0xFF36CA63),
        ["FirecrackerPlant"] = ("Symbol_Firecracker", 0xFFAE281E), ["DangleFruit"] = ("Symbol_DangleFruit", 0xFF0000FF),
        ["Mushroom"] = ("Symbol_Mushroom", 0xFFFFFFFF), ["JellyFish"] = ("Symbol_JellyFish", 0xFFA9A4B2),
        ["BubbleGrass"] = ("Symbol_BubbleGrass", 0xFF0EB23C), ["SlimeMold"] = ("Symbol_SlimeMold", 0xFFFF9900),
        ["Lantern"] = ("Symbol_Lantern", 0xFFFF9251), ["SporePlant"] = ("Symbol_SporePlant", 0xFFAE281E),
        ["FlyLure"] = ("Symbol_FlyLure", 0xFFAD4436), ["WaterNut"] = ("Symbol_WaterNut", 0xFF0D4DB3),
        ["GooieDuck"] = ("Symbol_GooieDuck", 0xFF72E6C4), ["DandelionPeach"] = ("Symbol_DandelionPeach", 0xFF96C7F5),
        ["LillyPuck"] = ("Symbol_LillyPuck", 0xFF2CF5FF), ["GlowWeed"] = ("Symbol_GlowWeed", 0xFFF2FF45),
        ["FireEgg"] = ("Symbol_FireEgg", 0xFFFF7878), ["EggBugEgg"] = ("Symbol_EggBugEgg", 0xFFFFFFFF),
        ["NeedleEgg"] = ("needleEggSymbol", 0xFF2D0D14), ["WhiteToken"] = ("Symbol_Satellite", 0xFFFFFFFF),
        ["DeadTokenStalk"] = ("Sandbox_Unlock", 0xFF888888)
    };

    internal static string IconFor(string type) => type == "ShelterMarker" ? type : "Object_" + type;

    internal static void RegisterObjectSprites()
    {
        foreach (var pair in Objects)
            if (CartographyAssets.Sprite(pair.Value.Sprite) is CartographyRaster sprite)
                CartographyAssets.Register("Object_" + pair.Key, sprite.Tint(pair.Value.Color));
    }

    internal static void LoadEmbedded()
    {
        using (Bitmap sheet = Sheet("Objects"))
        {
            void Add(string name, int x, int y, int w, int h) => Register(sheet, "Object_" + name, x, y, w, h);
            Add("KarmaFlower",76,0,23,23); Add("SeedCob",40,0,35,38); Add("GhostSpot",0,0,38,48);
            Add("BlueToken",76,24,10,20); Add("GoldToken",87,24,10,20); Add("RedToken",100,0,10,20);
            Add("DevToken",98,24,10,20); Add("GreenToken",111,0,10,20);
            Add("DataPearl",39,39,11,11); Add("UniqueDataPearl",39,39,11,11); Add("Slugcat",51,39,20,19);
            Add("ScavengerOutpost",109,21,11,15); Add("KarmaShrine",72,45,17,17); Add("MoonCloak",1,49,21,25);
        }
        using (Bitmap sheet = Sheet("MiscSprites"))
        {
            Register(sheet,"Misc_ArrowLeft",0,0,22,13); Register(sheet,"Misc_ArrowRight",0,13,22,13);
            Register(sheet,"Misc_KarmaR",23,0,36,36);
        }
        using (Bitmap sheet = Sheet("SlugcatIcons"))
        {
            string[] campaigns = { "White","Yellow","Red","Night","Gourmand","Artificer","Rivulet","Spear","Saint","Inv" };
            for (int n=0;n<campaigns.Length;n++) Register(sheet,"Slugcat_"+campaigns[n],n*20,26,20,19);
        }
    }

    private static Bitmap Sheet(string name)
    {
        using Stream stream = typeof(CartographyIconCatalog).Assembly.GetManifestResourceStream("DryCycle.Cartography." + name + ".png")
            ?? throw new InvalidDataException("Missing embedded cartography sprite sheet: " + name);
        using Bitmap loaded = new(stream);
        return new Bitmap(loaded);
    }

    private static void Register(Bitmap sheet, string name, int x, int y, int w, int h)
    {
        using Bitmap crop = sheet.Clone(new Rectangle(x,y,w,h), PixelFormat.Format32bppArgb);
        CartographyAssets.Register(name, CartographyRaster.FromBitmap(crop));
    }
}
