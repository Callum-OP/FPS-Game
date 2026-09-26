
/// <summary>
/// Turn-in-place footwork: while the character is idle (see CharacterLocomotion.IsIdle)
/// and its facing is changing - because PlayerMovement's camera-follow rotation, or Enemy.cs/
/// FriendlyAI.cs's own Quaternion.Slerp-toward-target rotation, is turning the transform -
/// this fades in the TurnInPlace Animator layer and feeds it a signed turn rate, so the legs actually pivot-step
/// instead of the whole body silently swivelling under a locomotion tree that has no idea
/// a turn is happening.
/// </summary>