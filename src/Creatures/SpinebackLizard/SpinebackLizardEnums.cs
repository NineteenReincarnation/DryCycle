namespace DryCycle.Creatures;

internal static class SpinebackLizardEnums
{
    internal static CreatureTemplate.Type Type { get; private set; }

    internal static void Register()
    {
        if (Type == null)
        {
            Type = new CreatureTemplate.Type("SpinebackLizard", register: true);
        }
    }
}
