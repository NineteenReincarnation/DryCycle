using DryCycle.Creatures.MantleCrab.Rendering;

internal static partial class Program
{
    private static void SpriteLayoutTests()
    {
        Check(MantleCrabSpriteLayout.LegStart == 0,
            "MantleCrab leg sprite block must remain the first allocation");
        Check(MantleCrabSpriteLayout.ThreadStart ==
              MantleCrabSpriteLayout.LegCount * MantleCrabSpriteLayout.LegStride,
            "Thread sprites must begin immediately after walking-leg allocations");
        Check(MantleCrabSpriteLayout.PincersStart == MantleCrabSpriteLayout.Shell + 1,
            "Pincer block must follow the mantle sprite");
        Check(MantleCrabSpriteLayout.EyeStalkStart ==
              MantleCrabSpriteLayout.PincersStart +
              MantleCrabSpriteLayout.PincerCount * MantleCrabSpriteLayout.PincerStride,
            "Eye stalk block overlaps the pincer allocation");
        Check(MantleCrabSpriteLayout.SpriteCount == MantleCrabSpriteLayout.EyesStart + 2,
            "SpriteCount no longer covers both eye meshes");

        bool[] occupied = new bool[MantleCrabSpriteLayout.SpriteCount];
        for (int leg = 0; leg < MantleCrabSpriteLayout.LegCount; leg++)
        {
            for (int segment = 0; segment < MantleCrabSpriteLayout.LegShaftCount; segment++)
                Occupy(occupied, MantleCrabSpriteLayout.LegShaft(leg, segment), "leg shaft");
            for (int joint = 0; joint < MantleCrabSpriteLayout.LegJointCount; joint++)
                Occupy(occupied, MantleCrabSpriteLayout.LegJoint(leg, joint), "leg joint");
            Occupy(occupied, MantleCrabSpriteLayout.LegFoot(leg), "leg foot");
        }

        for (int thread = 0; thread < MantleCrabSpriteLayout.ThreadCount; thread++)
            Occupy(occupied, MantleCrabSpriteLayout.Thread(thread), "fringe thread");
        Occupy(occupied, MantleCrabSpriteLayout.Shell, "shell");

        for (int pincer = 0; pincer < MantleCrabSpriteLayout.PincerCount; pincer++)
        {
            for (int segment = 0; segment < 4; segment++)
                Occupy(occupied, MantleCrabSpriteLayout.PincerShaft(pincer, segment), "pincer shaft");
            for (int joint = 0; joint < 3; joint++)
                Occupy(occupied, MantleCrabSpriteLayout.PincerJoint(pincer, joint), "pincer joint");
            Occupy(occupied, MantleCrabSpriteLayout.PincerPalm(pincer), "pincer palm");
            Occupy(occupied, MantleCrabSpriteLayout.PincerMovableFinger(pincer), "pincer finger");
        }

        for (int eye = 0; eye < 2; eye++)
        {
            Occupy(occupied, MantleCrabSpriteLayout.EyeStalk(eye), "eye stalk");
            Occupy(occupied, MantleCrabSpriteLayout.Eye(eye), "eye");
        }

        for (int i = 0; i < occupied.Length; i++)
            Check(occupied[i], "MantleCrab sprite layout left an unowned slot at index=" + i);
    }

    private static void Occupy(bool[] occupied, int index, string owner)
    {
        Check(index >= 0 && index < occupied.Length,
            owner + " escaped the MantleCrab sprite allocation at index=" + index);
        Check(!occupied[index],
            owner + " overlaps another MantleCrab sprite at index=" + index);
        occupied[index] = true;
    }
}
