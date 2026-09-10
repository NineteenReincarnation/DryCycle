using DryCycle.Creatures.MantleCrab.Rendering;
using UnityEngine;

internal static partial class Program
{
    private static void MaterialTests()
    {
        MantleCrabVisualPhenotype phenotype = CreatePhenotype(1729);

        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Foot,
            new Vector2(.46f, .18f),
            out Color innerFoot,
            out Color innerFootSurface);
        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Foot,
            new Vector2(.46f, .82f),
            out Color outerFoot,
            out Color outerFootSurface);
        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Foot,
            new Vector2(.97f, .5f),
            out _,
            out Color soleSurface);

        float footColorDifference = Mathf.Abs(innerFoot.r - outerFoot.r) +
                                    Mathf.Abs(innerFoot.g - outerFoot.g) +
                                    Mathf.Abs(innerFoot.b - outerFoot.b);
        Check(footColorDifference > .06f,
            "Foot material lost its authored inner/outer plate color split");
        Check(soleSurface.b > innerFootSurface.b + .45f && soleSurface.b > outerFootSurface.b + .45f,
            "Foot surface.B must remain a strong distal sole mask");
        Check(soleSurface.g > .82f,
            "Foot sole must remain rougher than ordinary dorsal chitin");

        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Joint,
            new Vector2(.5f, .5f),
            out Color jointCenter,
            out Color jointCenterSurface);
        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Joint,
            new Vector2(.08f, .08f),
            out Color jointEdge,
            out Color jointEdgeSurface);

        Check(jointCenterSurface.b > jointEdgeSurface.b + .35f,
            "Joint surface.B must identify the recessed membrane cavity");
        Check(jointCenterSurface.r < jointEdgeSurface.r,
            "Joint cavity must retain stronger ambient occlusion than the outer shell plate");
        Check(jointCenter.r + jointCenter.g + jointCenter.b < jointEdge.r + jointEdge.g + jointEdge.b,
            "Joint membrane must remain visibly darker than its surrounding shell");

        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Pincer,
            new Vector2(.82f, .96f),
            out _,
            out Color pincerEdgeSurface);
        MantleCrabMaterialBaker.Sample(
            phenotype,
            MantleCrabMaterial.Pincer,
            new Vector2(.82f, .5f),
            out _,
            out Color pincerCenterSurface);
        Check(pincerEdgeSurface.b > pincerCenterSurface.b + .35f,
            "Pincer surface.B must preserve the distal cutting-edge mask");
    }
}
