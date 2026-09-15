using System.Numerics;

namespace ItemForge.Core.Render;

// The one camera every card is viewed and baked through. It is ORTHOGRAPHIC on purpose: projection is then
// linear, so a 3D position maps to one exact pixel offset from the character's foot anchor, and that offset
// is the sprite pivot. Depth toward the camera never moves a pixel; it only decides what occludes what.
//
// The defaults are placeholders until the 1999 art is measured in phase 5; a measurement changes these
// numbers, not the maths.
public sealed record GameCamera
{
    public const float DefaultElevationDegrees = 30f;
    public const float DefaultPixelsPerUnit = 48f;
    public const int Directions = 8;

    // Degrees above the horizon. 0 would look along the ground, 90 straight down.
    public float ElevationDegrees { get; init; } = DefaultElevationDegrees;

    // Screen pixels per world unit at bake scale.
    public float PixelsPerUnit { get; init; } = DefaultPixelsPerUnit;

    // Which way the character faces: 0 = north, clockwise, 45 degrees per step (the client's order).
    public int Direction { get; init; }

    // Free-look only: extra yaw on top of the facing, for orbiting the model in the editor. A bake never
    // sets it - the whole point of the game camera is that every card is seen from the same angles.
    public float ExtraYawDegrees { get; init; }

    public float FacingDegrees => Direction * (360f / Directions) + ExtraYawDegrees;

    public bool IsGameCamera =>
        ExtraYawDegrees == 0f &&
        ElevationDegrees == DefaultElevationDegrees &&
        PixelsPerUnit == DefaultPixelsPerUnit;

    // World -> camera space. The character turns inside the camera, so the model rotates by the facing and
    // the view tips down by the elevation.
    public Matrix4x4 ViewMatrix =>
        Matrix4x4.CreateRotationY(Deg(FacingDegrees)) * Matrix4x4.CreateRotationX(-Deg(ElevationDegrees));

    // Camera space -> pixels, y down, relative to the foot anchor at the origin.
    public Vector2 ToPixels(Vector3 cameraSpace) =>
        new(cameraSpace.X * PixelsPerUnit, -cameraSpace.Y * PixelsPerUnit);

    // Pixels back to a camera-space point on a chosen depth plane, so typing a pixel value and dragging in
    // 3D stay in agreement.
    public Vector3 FromPixels(Vector2 pixels, float depth) =>
        new(pixels.X / PixelsPerUnit, -pixels.Y / PixelsPerUnit, depth);

    public GameCamera WithDirection(int direction) =>
        this with { Direction = ((direction % Directions) + Directions) % Directions };

    private static float Deg(float degrees) => degrees * MathF.PI / 180f;
}
