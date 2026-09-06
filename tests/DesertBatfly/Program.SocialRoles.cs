using System;
using DryCycle.Creatures.DesertBatfly;

internal static partial class Program
{
    // Historical name retained because Program.cs calls this entry point.  The
    // feature itself is rejected: this is now a regression guard ensuring that
    // social-role code cannot silently regain gameplay authority.
    private static void RunRoleDistribution()
    {
        int nonNone = 0;
        for (int seed = 0; seed < 10000; seed++)
        {
            var personality = new DesertBatflyPersonality(seed);
            var scores = DesertBatflyRoleScores.Calculate(personality);

            Check(scores.Sentinel == 0f && scores.Bully == 0f && scores.Opportunist == 0f,
                "rejected social-role scores remain neutral");
            Check(scores.Select(14, 0) == ExpressedSocialRole.None,
                "rejected social-role selector always returns None");
            Check(scores.Select(14, 14) == ExpressedSocialRole.None,
                "role population pressure cannot reactivate rejected roles");
            Check(scores.For(ExpressedSocialRole.Sentinel) == 0f &&
                  scores.For(ExpressedSocialRole.Bully) == 0f &&
                  scores.For(ExpressedSocialRole.Opportunist) == 0f,
                "legacy role symbols have zero behavioral score");

            if (scores.Select(14, 0) != ExpressedSocialRole.None)
                nonNone++;
        }

        Check(nonNone == 0, "10,000 personalities produce zero expressed social roles");
        Check(DesertBatflyRoleScores.EntryThreshold(14, 0) == 1f &&
              DesertBatflyRoleScores.EntryThreshold(14, 14) == 1f,
            "legacy entry threshold is inert");

        var compatibility = new DesertBatflyRoleScores(1f, 1f, 1f);
        Check(compatibility.Sentinel == 0f && compatibility.Bully == 0f && compatibility.Opportunist == 0f,
            "legacy constructor cannot manufacture role scores");
        Check(compatibility.Select(14, 0) == ExpressedSocialRole.None,
            "legacy constructor cannot reactivate a role");

        Console.WriteLine("Social Roles: REJECTED; 10,000 personalities verified with zero role expression and zero role modifiers.");
    }
}
