# Current features

## Local assets

Downloaded, supplied, and other Unity assets live
under `Assets/LocalAssets/`. I keep a local backup and have it ignored by Git to reduce chance of potential accidental redistribution of assets that I do not own.

# Current features

Player can move using wasd keys and press X to lower weapon and fast walk.
Can press Left Shift to aim and use Q to lean left or E to lean right (useful for peeking round corners).
Can press 1 to pickup weapons or objects, 2 to lower weapon or holster, and 3 to switch between them, as well as 4 to switch to grenades.
Can press R to reload and F to drop weapon.

Several weapon types, so far include 9mm handgun, 45 acp SMG, 12 gauge semi auto shotgun and full auto assult rifle. Each with different recoil, aim, bullet sizes and casings. Also included frag grenade and explosive barrels that shoot shrapnel upon explosion.

Enemies patrol the world and chase and shoot at player if they get too close, the player can manage to lose them if they get out of sight, enemies will investigate for a while before returning to patrol.

Has audio for shooting, reloading and damage and deaths, audio is unique to each weapon type or player type (there is only one player type so far).

A basic map layout, with the first scene being a club style location.

# Features to complete
Each gun should have limited mag counts.
Mags may be lying around the world and can be picked up, with enemies dropping relevant mag types as well as guns upon death.

Change pickups to show button to be pressed, only when the weapon or mag in front of player crosshair. As well as ideally pickup weapon at crosshair first, over any other weapons including closer ones.

Grenades.

# Features to consider
Crouch and stealth with option to do takedown on enemies if you manage to sneak up on them.

Extras? such as:
    Full auto toggle, Burst toggle, Semi Auto?
    Compensator or other accuracy boost
    Sight/Scope
    Torch/Flashlight
    Laser?

Other items or abilities?
    Armour/health bonus
    Hacking (get access to map cameras or environmental hazards)
    Expansion of grenades
    Instant takedown? (if they get close)
    Stealth takedown?

# Issues
Left leg is bent sideways, also several general issues with animations, will need to either check how clips are assigned to be played or look at the animations of the clips themselves and swap them out if necessary.

Stepping on a newly formed ragdoll from recent AI death it causes ragdoll body to teleport up and fall back down.

Add more stumble animations during death when running/walking.

If you holster gun with it is lowered it can appear weird in holster. This is when holstering, switching to weapon or switching to grenade. Changing to grenade sometimes doesn't let you change away from it.

Hands do not always stick firmly to grip points on weapon especially during high speed turning or looking up.

Currently Anatomical Constraints causes issues with animations (especially friendly and enemy AIs) and has currently been disabled for causing more harm than good, but may be worth revisiting.

Some sounds are louder and quieter than they should be, add a way to change the volume. Not too bad at the moment.

It would probably make more sense if leaning disabled the fast walk