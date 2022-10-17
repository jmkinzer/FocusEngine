using System;
using System.Collections.Generic;
using System.Text;
using BepuPhysics.Collidables;
using Xenko.Core;
using Xenko.Core.Mathematics;
using Xenko.Engine;
using Xenko.Games;
using Xenko.VirtualReality;

namespace Xenko.Physics.Bepu
{
    /// <summary>
    /// Helper object for handling player movement with a base physical body and a VR head attached to a neck. This can be used without VR too, but will just act like a simple character controller.
    /// </summary>
    public class BepuCharacterController
    {
        /// <summary>
        /// Generated rigidbody
        /// </summary>
        public BepuRigidbodyComponent Body { get; internal set; }

        /// <summary>
        /// Camera, if found off of the baseBody
        /// </summary>
        public CameraComponent Camera { get; internal set; }

        private Game internalGame;
        private bool VR;

        public float Height { get; internal set; }
        public float Width { get; internal set; }

        private Dictionary<Vector2, Capsule> CapsuleCache = new Dictionary<Vector2, Capsule>();

        /// <summary>
        /// Make a new BepuCharacterController helper for an entity, also useful for VR. Automatically will break off VR-tracked from Camera to base if using VR
        /// </summary>
        public BepuCharacterController(Entity baseBody, float height = 1.7f, float width = 0.5f, bool VRMode = false, CollisionFilterGroups physics_group = CollisionFilterGroups.CharacterFilter,
                                       CollisionFilterGroupFlags collides_with = CollisionFilterGroupFlags.StaticFilter | CollisionFilterGroupFlags.KinematicFilter |
                                       CollisionFilterGroupFlags.EnemyFilter | CollisionFilterGroupFlags.CharacterFilter, HashSet<Entity> AdditionalVREntitiesToDisconnectFromCamera = null)
        {
            float capsule_len = height - width * 2f;
            if (capsule_len <= 0f)
                throw new ArgumentOutOfRangeException("Height cannot be less than 2*width for capsule shape (BepuCharacterController for " + baseBody.Name);

            Height = height;
            Width = width;

            var cap = new Capsule(width, capsule_len);
            CapsuleCache[new Vector2(width, height)] = cap;

            Body = new BepuRigidbodyComponent(cap);
            Body.CollisionGroup = physics_group;
            Body.CanCollideWith = collides_with;
            VR = VRMode;

            if (AdditionalVREntitiesToDisconnectFromCamera == null)
                AdditionalVREntitiesToDisconnectFromCamera = new HashSet<Entity>();

            // can we find an attached camera?
            foreach(Entity e in baseBody.GetChildren())
            {
                if (Camera == null) Camera = e.Get<CameraComponent>();
                if (e.Transform.TrackVRHand != TouchControllerHand.None)
                    AdditionalVREntitiesToDisconnectFromCamera.Add(e);
            }

            if (Camera != null && VRMode)
            {
                foreach (Entity e in AdditionalVREntitiesToDisconnectFromCamera)
                    if (e.Transform.Parent == Camera.Entity.Transform) e.Transform.Parent = Body.Entity.Transform;
            }

            Body.AttachEntityAtBottom = true;
            Body.IgnorePhysicsRotation = true;
            Body.IgnorePhysicsPosition = VR && Camera != null;
            Body.RotationLock = true;
            Body.ActionPerSimulationTick += UpdatePerSimulationTick;

            baseBody.Add(Body);

            internalGame = ServiceRegistry.instance?.GetService<IGame>() as Game;
        }

        /// <summary>
        /// If flying is true, gravity is zero and Donttouch_Y is false
        /// </summary>
        public bool Flying
        {
            get => Body.OverrideGravity;
            set
            {
                Body.OverrideGravity = value;
                Body.Gravity = Vector3.Zero;
                DontTouch_Y = !value;
            }
        }

        /// <summary>
        /// This uses some CPU, but can monitor things like OnGround() functionality
        /// </summary>
        public bool TrackCollisions
        {
            set
            {
                Body.CollectCollisionMaximumCount = value ? 8 : 0;
                Body.CollectCollisions = value;
            }
            get => Body.CollectCollisions;
        }

        /// <summary>
        /// Returns a contact if this is considered on the ground. Requires TrackCollisions to be true
        /// </summary>
        /// <param name="threshold"></param>
        /// <returns></returns>
        public BepuContact? OnGround(float threshold = 0.75f)
        {
            if (TrackCollisions == false)
                throw new InvalidOperationException("You need to set TrackCollisions to true for OnGround to work for CharacterCollision: " + Body.Entity.Name);

            try
            {
                Vector3 reverseGravity = -(Body.OverrideGravity ? Body.Gravity : BepuSimulation.instance.Gravity);
                reverseGravity.Normalize();
                for (int i = 0; i < Body.CurrentPhysicalContactsCount; i++)
                {
                    var contact = Body.CurrentPhysicalContacts[i];
                    if (Vector3.Dot(contact.Normal, reverseGravity) > threshold) return contact;
                }
            }
            catch (Exception) { }
            return null;
        }

        /// <summary>
        /// This can change the shape of the rigidbody easily
        /// </summary>
        public void Resize(float? height, float? width = null, bool reposition = true)
        {
            float useh = height ?? Height;
            float usew = width ?? Width;

            if (useh == Height && usew == Width) return;

            var key = new Vector2(usew, useh);
            if (CapsuleCache.TryGetValue(key, out var capsule))
                Body.ColliderShape = capsule;
            else
            {
                float capsule_len = useh - usew * 2f;
                if (capsule_len <= 0f)
                    throw new ArgumentOutOfRangeException("Height cannot be less than 2*width for capsule shape (BepuCharacterController for " + Body.Entity.Name);

                var cap = new BepuPhysics.Collidables.Capsule(usew, capsule_len);
                CapsuleCache[key] = cap;
                Body.ColliderShape = cap;
            }

            Height = useh;
            Width = usew;

            if (reposition) SetPosition(Body.Entity.Transform.Position);
        }

        /// <summary>
        /// Jump! Will set ApplySingleImpulse (overwriting anything that was there already)
        /// </summary>
        public void Jump(float amount)
        {
            ApplySingleImpulse = new Vector3(0f, amount, 0f);
        }

        /// <summary>
        /// Set how you want this character to move
        /// </summary>
        public Vector3 DesiredMovement;

        /// <summary>
        /// How to dampen the different axis during updating? Defaults to (15,0,15)
        /// </summary>
        public Vector3? MoveDampening = new Vector3(15f, 0f, 15f);

        /// <summary>
        /// Applying a single impulse to this (useful for jumps or pushes)
        /// </summary>
        public Vector3? ApplySingleImpulse;

        /// <summary>
        /// Only operate on X/Z? Useful for non-flying characters
        /// </summary>
        public bool DontTouch_Y = true;

        /// <summary>
        /// Push the character with forces (true) or set velocity directly (false)
        /// </summary>
        public bool UseImpulseMovement = true;

        /// <summary>
        /// Multiplier for the impulse movement (defaults to 100)
        /// </summary>
        public float ImpulseMovementMultiplier = 125f;

        /// <summary>
        /// Multiplier for velocity movement (defaults to 3)
        /// </summary>
        public float VelocityMovementMultiplier = 3f;

        /// <summary>
        /// How height to set the camera when positioning, if using camera?
        /// </summary>
        public float CameraHeightPercent = 0.95f;

        /// <summary>
        /// If you'd like to perform a physics tick action on this rigidbody, use this
        /// </summary>
        public Action<BepuRigidbodyComponent, float> AdditionalPerPhysicsAction = null;

        private Vector3 oldPos;

        private void UpdatePerSimulationTick(BepuRigidbodyComponent _body, float frame_time)
        {
            // make sure we are awake if we want to be moving
            if (Body.InternalBody.Awake == false)
                Body.InternalBody.Awake = DesiredMovement != Vector3.Zero || ApplySingleImpulse.HasValue || TrackCollisions;

            if (Body.IgnorePhysicsPosition)
            {
                // use the last velocity to move our base
                Body.Entity.Transform.Position += (Body.Position - oldPos);
                oldPos = Body.Position;
            }

            // try to push our body
            if (UseImpulseMovement)
            {
                // get rid of y if we are not operating on it
                if (DontTouch_Y) DesiredMovement.Y = 0f;
                Body.InternalBody.ApplyLinearImpulse(BepuHelpers.ToBepu(DesiredMovement * frame_time * Body.Mass * ImpulseMovementMultiplier));
            }
            else if (DontTouch_Y)
            {
                Vector3 originalVel = Body.LinearVelocity;
                Vector3 newmove = new Vector3(DesiredMovement.X * VelocityMovementMultiplier, originalVel.Y, DesiredMovement.Z * VelocityMovementMultiplier);
                Body.InternalBody.Velocity.Linear = BepuHelpers.ToBepu(newmove);
            }
            else Body.InternalBody.Velocity.Linear = BepuHelpers.ToBepu(DesiredMovement * VelocityMovementMultiplier);

            // single impulse to apply?
            if (ApplySingleImpulse.HasValue)
            {
                Body.InternalBody.ApplyLinearImpulse(BepuHelpers.ToBepu(ApplySingleImpulse.Value));
                ApplySingleImpulse = null;
            }

            // apply MoveDampening, if any
            if (MoveDampening != null)
            {
                var vel = Body.InternalBody.Velocity.Linear;
                vel.X *= 1f - frame_time * MoveDampening.Value.X;
                vel.Y *= 1f - frame_time * MoveDampening.Value.Y;
                vel.Z *= 1f - frame_time * MoveDampening.Value.Z;
                Body.InternalBody.Velocity.Linear = vel;
            }

            // do we need to move our base body toward our camera head?
            if (Camera != null && VR)
            {
                Vector3 finalpos = Camera.Entity.Transform.WorldPosition();
                if (DontTouch_Y) finalpos.Y = Body.Position.Y;
                float xDist = Body.Position.X - finalpos.X;
                float yDist = Body.Position.Y - finalpos.Y;
                float zDist = Body.Position.Z - finalpos.Z;
                if (xDist * xDist + yDist * yDist + zDist * zDist > 0.1f)
                {
                    Vector3 gravitybump = -(Body.OverrideGravity ? Body.Gravity : BepuSimulation.instance.Gravity);
                    gravitybump.Normalize();
                    gravitybump *= 0.05f;
                    var result = BepuSimulation.instance.ShapeSweep<Capsule>((Capsule)Body.ColliderShape, Body.Position + gravitybump, Body.Rotation, finalpos + gravitybump, Body.CanCollideWith, Body);
                    if (result.Succeeded == false)
                    {
                        Body.Position = finalpos;
                        oldPos = finalpos;
                    }
                }
            }

            if (AdditionalPerPhysicsAction != null)
                AdditionalPerPhysicsAction(_body, frame_time);
        }

        /// <summary>
        /// If you have a FOV reduction vignette thing in VR, this flag will tell you to use it (smooth turning, for example)
        /// </summary>
        public bool ShouldHaveFOVReduction { get; internal set; }

        private float desiredPitch, pitch, yaw, desiredYaw;
        private bool shouldFlickTurn = true;

        /// <summary>
        /// Use this to handle mouse/VR look, which operates on a camera (if found)
        /// </summary>
        public void HandleMouseAndVRLook(float frame_time, float mouse_sensitivity = 3f, bool Invert_Y = false, bool VRSmoothTurn = false, float VRSnapTurnAmount = 45f, bool VRPressToTurn = false)
        {
            if (Camera == null)
                throw new ArgumentNullException("No camera to look with!");

            if (VR)
            {
                bool fov_check = false;

                if (VRSmoothTurn)
                {
                    // smooth turning
                    var rightController = VRDeviceSystem.GetSystem?.GetController(TouchControllerHand.Right);
                    if (rightController != null)
                    {
                        // wait, are we suppose to be pressing?
                        if (VRPressToTurn && rightController.IsPressed(TouchControllerButton.Thumbstick) == false) return;
                        // are we pushing enough?
                        Vector2 thumb = rightController.ThumbstickAxis;
                        if (thumb.X > 0.1f || thumb.X < -0.1f)
                        {
                            Body.Entity.Transform.Rotation *= global::Xenko.Core.Mathematics.Quaternion.RotationYDeg(thumb.X * frame_time * -125f);
                            fov_check = true;
                        }
                    }
                }
                else
                {
                    // snap turning
                    if (VRPressToTurn)
                    {
                        if (VRButtons.LeftThumbstickLeft.IsPressed())
                            Body.Entity.Transform.Rotation *= Quaternion.RotationYDeg(VRSnapTurnAmount);
                        else if (VRButtons.LeftThumbstickRight.IsPressed())
                            Body.Entity.Transform.Rotation *= Quaternion.RotationYDeg(-VRSnapTurnAmount);
                    }
                    else
                    {
                        // flick to snap turn
                        var rightController = VRDeviceSystem.GetSystem?.GetController(TouchControllerHand.Right);
                        if (rightController != null)
                        {
                            Vector2 thumb = rightController.ThumbstickAxis;
                            if (thumb.X > 0.5f)
                            {
                                if (shouldFlickTurn)
                                {
                                    Body.Entity.Transform.Rotation *= Quaternion.RotationYDeg(-VRSnapTurnAmount);
                                    shouldFlickTurn = false;
                                }
                            }
                            else if (thumb.X < -0.5f)
                            {
                                if (shouldFlickTurn)
                                {
                                    Body.Entity.Transform.Rotation *= Quaternion.RotationYDeg(VRSnapTurnAmount);
                                    shouldFlickTurn = false;
                                }
                            }
                            else shouldFlickTurn = true;
                        }
                    }
                }

                ShouldHaveFOVReduction = fov_check;

                return;
            }

            Vector2 rotationDelta = internalGame.Input.MouseDelta;

            // Take shortest path
            float deltaPitch = desiredPitch - pitch;
            float deltaYaw = (desiredYaw - yaw) % MathUtil.TwoPi;
            if (deltaYaw < 0) deltaYaw += MathUtil.TwoPi;
            if (deltaYaw > MathUtil.Pi) deltaYaw -= MathUtil.TwoPi;
            desiredYaw = yaw + deltaYaw;

            // Perform orientation transition
            yaw = Math.Abs(deltaYaw) < frame_time ? desiredYaw : yaw + frame_time * Math.Sign(deltaYaw);
            pitch = Math.Abs(deltaPitch) < frame_time ? desiredPitch : pitch + frame_time * Math.Sign(deltaPitch);

            desiredYaw = yaw -= 1.333f * rotationDelta.X * mouse_sensitivity; // we want to rotate faster Horizontally and Vertically
            desiredPitch = pitch = MathUtil.Clamp(pitch - rotationDelta.Y * (Invert_Y ? -mouse_sensitivity : mouse_sensitivity), -MathUtil.PiOverTwo + 0.05f, MathUtil.PiOverTwo - 0.05f);

            Camera.Entity.Transform.Rotation = Quaternion.RotationYawPitchRoll(yaw, pitch, 0);
        }

        /// <summary>
        /// Set our position and center the camera (if used) on this
        /// </summary>
        public void SetPosition(Vector3 position)
        {
            if (Camera != null && !VR) {
                Camera.Entity.Transform.Position.X = 0f;
                Camera.Entity.Transform.Position.Y = Height * CameraHeightPercent;
                Camera.Entity.Transform.Position.Z = 0f;
            }
            Body.Entity.Transform.Position = position;
            position.Y += Height * 0.5f;
            Body.Position = position;
            oldPos = position;
        }
    }
}
