using System;
using System.Linq;
using System.Reflection;
using DryCycle.Creatures.MantleCrab.Rendering;

internal static partial class Program
{
    private static MantleCrabVisualPhenotype CreatePhenotype(int seed, float dominance = .5f)
    {
        AbstractCreature creature = Empty<AbstractCreature>();
        creature.ID = new EntityID(-1, seed);
        creature.personality = new AbstractCreature.Personality
        {
            energy = .5f,
            bravery = .5f,
            sympathy = .5f,
            dominance = dominance,
            nervous = .5f,
            aggression = .5f
        };

        return new MantleCrabVisualPhenotype(new MantleCrabVisualGenome(creature));
    }

    private static void GenomeTests()
    {
        FieldInfo[] phenotypeFields = typeof(MantleCrabVisualPhenotype)
            .GetFields(Flags)
            .Where(field => field.FieldType == typeof(float) || field.FieldType == typeof(UnityEngine.Vector4))
            .ToArray();
        string[] channels = { "ShellHue", "StripePhase", "PlateSeed", "EyeSize" };

        for (int seed = -32; seed < 32; seed++)
        {
            foreach (string channel in channels)
            {
                float first = MantleCrabVisualGenome.Hash(seed, channel);
                float second = MantleCrabVisualGenome.Hash(seed, channel);
                Check(first == second, "Genome hash is not deterministic for " + channel + " seed=" + seed);
                Check(first >= 0f && first < 1f, "Genome hash escaped [0,1): " + channel + " seed=" + seed);
            }

            MantleCrabVisualPhenotype a = CreatePhenotype(seed);
            MantleCrabVisualPhenotype b = CreatePhenotype(seed);
            bool identical = true;
            foreach (FieldInfo field in phenotypeFields)
            {
                if (!Equals(field.GetValue(a), field.GetValue(b)))
                {
                    identical = false;
                    break;
                }
            }
            Check(identical, "Phenotype reload drift for seed=" + seed);

            MantleCrabVisualPhenotype dominant = CreatePhenotype(seed, 1f);
            Check(a.ShellWidth >= .96f && a.ShellWidth <= 1.04f, "Species shell width escaped bounds");
            Check(a.EyeSize >= 3.6f && a.EyeSize <= 4.01f, "Eye size escaped species bounds");
            Check(a.FootBulk >= .92f && a.FootBulk <= 1.17f, "Foot bulk escaped species bounds");
            Check(a.Asymmetry >= .02f && a.Asymmetry <= .061f, "Visual asymmetry escaped species bounds");
            Check(a.Hue == dominant.Hue, "Dominance must not change shell hue");
            genomeCases++;
        }

        float before = MantleCrabVisualGenome.Hash(1729, "ShellHue");
        _ = MantleCrabVisualGenome.Hash(1729, "UnrelatedChannel");
        Check(before == MantleCrabVisualGenome.Hash(1729, "ShellHue"), "Named genome channels unexpectedly share mutable state");
    }
}
