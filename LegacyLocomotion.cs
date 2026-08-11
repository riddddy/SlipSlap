using System.Reflection;
using HarmonyLib;
using UnityEngine;
using GorillaLocomotion;

namespace slipslap
{
    // Old build's locomotion, ported. Runs in place of Player.LateUpdate.
    //
    // Old build put all of this in Update() and had a one-line FixedUpdate;
    // the current build moved it to LateUpdate. Ported: the main loop, the
    // 9-arg IterativeCollisionSphereCast (returns surfaceNormal), the 6-arg
    // CollisionsSphereCast (takes precision), StoreVelocities, AntiTeleport
    // and the hand position helpers.
    //
    // GetSlidePercentage is NOT ported - it's called on the game instance by
    // reflection, since it reads this build's material table and sets
    // currentMaterialIndex/currentOverride, which the code below reads back.
    //
    // Old private state lives here rather than on Player. Anything other
    // systems read gets written back each frame.
    public static class LegacyLocomotion
    {
        // Dropped from the current build (stickDepth is a different thing).
        public const float SlideStickDistance = 0.03f;

        public static bool Enabled = true;

        // false = never report sliding to GorillaTagger, so surfaces give one
        // contact tap instead of a continuous buzz. Physics reads the slide
        // flags directly and doesn't care.
        public static bool ContinuousSlideFeedback = false;

        // Only used when ContinuousSlideFeedback is on: buzz on ice only.
        public static bool StrictSlidingFeedback = true;

        public static bool LogLaunches = false;

        // ---- ported state -------------------------------------------
        static bool ready;
        static Vector3 lastLeftHandPosition, lastRightHandPosition, lastHeadPosition, lastPosition;
        static Vector3[] velocityHistory = new Vector3[0];
        static int velocityIndex;
        static Vector3 currentVelocity, denormalizedVelocityAverage;
        static Vector3 slideAverage, slideAverageNormal;
        static float calcDeltaTime, lastRealTime;
        static bool leftHandColliding, rightHandColliding;

        // ---- fields that stay on Player ------------------------------
        static readonly AccessTools.FieldRef<Player, Rigidbody> rigidBodyRef =
            AccessTools.FieldRefAccess<Player, Rigidbody>("playerRigidBody");
        static readonly AccessTools.FieldRef<Player, Vector3> denormRef =
            AccessTools.FieldRefAccess<Player, Vector3>("denormalizedVelocityAverage");
        static readonly AccessTools.FieldRef<Player, Vector3> gameSlideAverageRef =
            AccessTools.FieldRefAccess<Player, Vector3>("slideAverage");
        static readonly AccessTools.FieldRef<Player, Vector3> gameSlideNormalRef =
            AccessTools.FieldRefAccess<Player, Vector3>("slideAverageNormal");
        static readonly AccessTools.FieldRef<Player, float> gameCalcDeltaRef =
            AccessTools.FieldRefAccess<Player, float>("calcDeltaTime");
        static readonly AccessTools.FieldRef<Player, bool> gameLeftCollidingRef =
            AccessTools.FieldRefAccess<Player, bool>("leftHandColliding");
        static readonly AccessTools.FieldRef<Player, bool> gameRightCollidingRef =
            AccessTools.FieldRefAccess<Player, bool>("rightHandColliding");
        static readonly AccessTools.FieldRef<Player, Vector3> bodyOffsetVectorRef =
            AccessTools.FieldRefAccess<Player, Vector3>("bodyOffsetVector");

        static MethodInfo? getSlidePercentage;
        static MethodInfo? bodyCollider;

        // ---------------------------------------------------------
        public static void Reset() => ready = false;

        static void Init(Player p)
        {
            var rb = rigidBodyRef(p);
            int size = Mathf.Max(1, p.velocityHistorySize);
            velocityHistory = new Vector3[size];
            for (int i = 0; i < velocityHistory.Length; i++) velocityHistory[i] = Vector3.zero;

            lastLeftHandPosition = p.leftHandFollower.transform.position;
            lastRightHandPosition = p.rightHandFollower.transform.position;
            lastHeadPosition = p.headCollider.transform.position;
            p.lastHeadPosition = lastHeadPosition;
            lastPosition = p.transform.position;
            velocityIndex = 0;
            denormalizedVelocityAverage = Vector3.zero;
            slideAverage = Vector3.zero;
            slideAverageNormal = Vector3.up;
            lastRealTime = Time.realtimeSinceStartup;

            getSlidePercentage = AccessTools.Method(typeof(Player), "GetSlidePercentage",
                new System.Type[] { typeof(RaycastHit) });
            if (getSlidePercentage == null)
                Debug.LogError("[SlipSlap] GetSlidePercentage not found - every surface will read as default grip.");

            bodyCollider = AccessTools.Method(typeof(Player), "BodyCollider", new System.Type[0]);
            if (bodyCollider == null)
                Debug.LogWarning("[SlipSlap] BodyCollider() not found - body capsule won't be resized.");

            ready = true;
            Debug.Log("[SlipSlap] Legacy locomotion initialised.");
        }

        // Old Update(), in order. Returns false to skip the original.
        public static bool Run(Player p)
        {
            if (!Enabled) return true;
            if (p.headCollider == null || p.bodyCollider == null ||
                p.leftHandFollower == null || p.rightHandFollower == null) return true;

            var rb = rigidBodyRef(p);
            if (rb == null) return true;

            if (!ready) Init(p);

            leftHandColliding = false;
            rightHandColliding = false;
            p.leftHandSlide = false;
            p.rightHandSlide = false;
            Vector3 rigidBodyMovement = Vector3.zero;
            Vector3 firstIterationLeftHand = Vector3.zero;
            Vector3 firstIterationRightHand = Vector3.zero;
            p.rightHandSlideNormal = Vector3.up;
            p.leftHandSlideNormal = Vector3.up;

            p.bodyCollider.transform.position =
                PositionWithOffset(p.headCollider.transform, p.bodyOffset) + SafeBodyOffsetVector(p);
            p.bodyCollider.transform.eulerAngles =
                new Vector3(0f, p.headCollider.transform.eulerAngles.y, 0f);

            if (p.debugMovement)
            {
                calcDeltaTime = Time.deltaTime;
                lastRealTime = Time.time;
            }
            else
            {
                float now = Time.realtimeSinceStartup;
                calcDeltaTime = now - lastRealTime;
                lastRealTime = now;
                if (calcDeltaTime > 0.1f) calcDeltaTime = 0.05f;
            }
            if (calcDeltaTime <= 0f) calcDeltaTime = 0.011f;
            gameCalcDeltaRef(p) = calcDeltaTime;

            AntiTeleport(p, rb);

            if (p.wasLeftHandSlide || p.wasRightHandSlide)
            {
                p.transform.position += slideAverage * calcDeltaTime;
                rb.velocity = slideAverage + Vector3.down * 9.8f * calcDeltaTime;
            }
            slideAverage = Vector3.zero;

            Vector3 distanceTraveled;
            Vector3 finalPosition;
            float slipPercentage;
            Vector3 surfaceNormal;
            RaycastHit tempHitInfo;
            RaycastHit hitInfo;

            // ---- left hand ----
            distanceTraveled = p.wasLeftHandSlide
                ? CurrentLeftHandPosition(p) - lastLeftHandPosition +
                  slideAverageNormal.normalized * (0f - SlideStickDistance) * 9.8f * calcDeltaTime * calcDeltaTime
                : CurrentLeftHandPosition(p) - lastLeftHandPosition +
                  Vector3.down * 2f * 9.8f * calcDeltaTime * calcDeltaTime;

            if (IterativeCollisionSphereCast(p, lastLeftHandPosition, p.minimumRaycastDistance, distanceTraveled,
                    p.defaultPrecision, out finalPosition, true, out slipPercentage, out surfaceNormal, out tempHitInfo))
            {
                p.leftHandSlideNormal = surfaceNormal;

                firstIterationLeftHand =
                    (p.wasLeftHandTouching && (slipPercentage == p.defaultSlideFactor || slipPercentage == 0.001f))
                        ? lastLeftHandPosition - CurrentLeftHandPosition(p)
                        : finalPosition - CurrentLeftHandPosition(p);

                p.leftHandSlipPercentage = slipPercentage;
                p.leftHandSlide = slipPercentage > p.defaultSlideFactor;

                if (p.leftHandSlide && Physics.Raycast(finalPosition, tempHitInfo.point - finalPosition,
                        out hitInfo, (tempHitInfo.point - finalPosition).magnitude * 1.05f, p.locomotionEnabledLayers.value))
                    p.leftHandSlideNormal = hitInfo.normal;
                else
                    p.leftHandSlide = false;

                leftHandColliding = true;
                p.leftHandMaterialTouchIndex = p.currentMaterialIndex;
                p.leftHandSurfaceOverride = p.currentOverride;
            }

            // ---- right hand ----
            distanceTraveled = p.wasRightHandSlide
                ? CurrentRightHandPosition(p) - lastRightHandPosition +
                  slideAverageNormal.normalized * (0f - SlideStickDistance) * 9.8f * calcDeltaTime * calcDeltaTime
                : CurrentRightHandPosition(p) - lastRightHandPosition +
                  Vector3.down * 2f * 9.8f * calcDeltaTime * calcDeltaTime;

            if (IterativeCollisionSphereCast(p, lastRightHandPosition, p.minimumRaycastDistance, distanceTraveled,
                    p.defaultPrecision, out finalPosition, true, out slipPercentage, out surfaceNormal, out tempHitInfo))
            {
                p.rightHandSlideNormal = surfaceNormal;

                firstIterationRightHand =
                    (p.wasRightHandTouching && (slipPercentage == p.defaultSlideFactor || slipPercentage == 0.001f))
                        ? lastRightHandPosition - CurrentRightHandPosition(p)
                        : finalPosition - CurrentRightHandPosition(p);

                p.rightHandSlipPercentage = slipPercentage;
                p.rightHandSlide = slipPercentage > p.defaultSlideFactor;

                if (p.rightHandSlide && Physics.Raycast(finalPosition, tempHitInfo.point - finalPosition,
                        out hitInfo, (tempHitInfo.point - finalPosition).magnitude * 1.05f, p.locomotionEnabledLayers.value))
                    p.rightHandSlideNormal = hitInfo.normal;
                else
                    p.rightHandSlide = false;

                rightHandColliding = true;
                p.rightHandMaterialTouchIndex = p.currentMaterialIndex;
                p.rightHandSurfaceOverride = p.currentOverride;
            }

            // ---- body ----
            rigidBodyMovement =
                ((leftHandColliding || p.wasLeftHandTouching) && (rightHandColliding || p.wasRightHandTouching))
                    ? (firstIterationLeftHand + firstIterationRightHand) / 2f
                    : firstIterationLeftHand + firstIterationRightHand;

            if (IterativeCollisionSphereCast(p, lastHeadPosition, p.headCollider.radius,
                    p.headCollider.transform.position + rigidBodyMovement - lastHeadPosition,
                    p.defaultPrecision, out finalPosition, false, out slipPercentage, out surfaceNormal, out tempHitInfo))
            {
                rigidBodyMovement = finalPosition - lastHeadPosition;
            }

            if (rigidBodyMovement != Vector3.zero &&
                Physics.Raycast(lastHeadPosition,
                    p.headCollider.transform.position - lastHeadPosition + rigidBodyMovement, out hitInfo,
                    (p.headCollider.transform.position - lastHeadPosition + rigidBodyMovement).magnitude +
                        p.headCollider.radius * p.defaultPrecision * 0.999f, p.locomotionEnabledLayers.value))
            {
                rigidBodyMovement = lastHeadPosition - p.headCollider.transform.position;
            }
            else if (rigidBodyMovement == Vector3.zero &&
                Physics.Raycast(lastHeadPosition, p.headCollider.transform.position - lastHeadPosition, out hitInfo,
                    (p.headCollider.transform.position - lastHeadPosition).magnitude +
                        p.headCollider.radius * p.defaultPrecision * 0.999f, p.locomotionEnabledLayers.value))
            {
                rigidBodyMovement = lastHeadPosition - p.headCollider.transform.position;
            }

            if (rigidBodyMovement != Vector3.zero)
                p.transform.position += rigidBodyMovement;

            lastHeadPosition = p.headCollider.transform.position;

            // Must mirror: FixedUpdate is still the game's and is just
            // AntiTeleportTechnology(), which reads Player.lastHeadPosition.
            // Leave it stale and it drags you back every physics step.
            p.lastHeadPosition = lastHeadPosition;

            // ---- settle hands ----
            bool singleHand = (!leftHandColliding && !p.wasLeftHandTouching) ||
                              (!rightHandColliding && !p.wasRightHandTouching);

            distanceTraveled = CurrentLeftHandPosition(p) - lastLeftHandPosition;
            if (IterativeCollisionSphereCast(p, lastLeftHandPosition, p.minimumRaycastDistance, distanceTraveled,
                    p.defaultPrecision, out finalPosition, singleHand, out slipPercentage, out surfaceNormal, out tempHitInfo))
            {
                lastLeftHandPosition = finalPosition;
                leftHandColliding = true;
                p.leftHandMaterialTouchIndex = p.currentMaterialIndex;
                p.leftHandSurfaceOverride = p.currentOverride;
            }
            else lastLeftHandPosition = CurrentLeftHandPosition(p);

            distanceTraveled = CurrentRightHandPosition(p) - lastRightHandPosition;
            if (IterativeCollisionSphereCast(p, lastRightHandPosition, p.minimumRaycastDistance, distanceTraveled,
                    p.defaultPrecision, out finalPosition, singleHand, out slipPercentage, out surfaceNormal, out tempHitInfo))
            {
                lastRightHandPosition = finalPosition;
                rightHandColliding = true;
                p.rightHandMaterialTouchIndex = p.currentMaterialIndex;
                p.rightHandSurfaceOverride = p.currentOverride;
            }
            else lastRightHandPosition = CurrentRightHandPosition(p);

            StoreVelocities(p);

            // ---- the slide block ----
            if ((p.rightHandSlide || p.leftHandSlide) &&
                p.leftHandSlide == leftHandColliding && p.rightHandSlide == rightHandColliding)
            {
                if (p.rightHandSlide && p.leftHandSlide)
                {
                    slideAverageNormal = (p.rightHandSlideNormal + p.leftHandSlideNormal) / 2f;
                    if (Vector3.Dot(slideAverageNormal, rb.velocity) < 0f)
                    {
                        slideAverage = (Vector3.ProjectOnPlane(rb.velocity, p.rightHandSlideNormal) *
                                            (1f - (1f - p.rightHandSlipPercentage) * p.frictionConstant * calcDeltaTime) +
                                        Vector3.ProjectOnPlane(rb.velocity, p.leftHandSlideNormal) *
                                            (1f - (1f - p.leftHandSlipPercentage) * p.frictionConstant * calcDeltaTime)) / 2f;
                    }
                    else slideAverage = rb.velocity;
                }
                else if (p.rightHandSlide)
                {
                    slideAverageNormal = p.rightHandSlideNormal;
                    if (Vector3.Dot(slideAverageNormal, rb.velocity) < 0f)
                    {
                        slideAverage = Vector3.ProjectOnPlane(rb.velocity, p.rightHandSlideNormal) *
                                       (1f - (1f - p.rightHandSlipPercentage) * p.frictionConstant * calcDeltaTime);
                        Vector3 dir = Vector3.ProjectOnPlane(p.rightHandTransform.forward, p.rightHandSlideNormal);
                        slideAverage = SteerAlong(slideAverage, dir, p.slideControl);
                    }
                    else slideAverage = rb.velocity;
                }
                else if (p.leftHandSlide)
                {
                    slideAverageNormal = p.leftHandSlideNormal;
                    if (Vector3.Dot(slideAverageNormal, rb.velocity) < 0f)
                    {
                        slideAverage = Vector3.ProjectOnPlane(rb.velocity, p.leftHandSlideNormal) *
                                       (1f - (1f - p.leftHandSlipPercentage) * p.frictionConstant * calcDeltaTime);
                        // rightHandSlideNormal is not a typo, the original does this
                        Vector3 dir = Vector3.ProjectOnPlane(p.leftHandTransform.forward, p.rightHandSlideNormal);
                        slideAverage = SteerAlong(slideAverage, dir, p.slideControl);
                    }
                    else slideAverage = rb.velocity;
                }

                if ((p.wasLeftHandSlide || p.wasRightHandSlide) &&
                    rb.velocity.magnitude > slideAverage.magnitude && slideAverage.sqrMagnitude > 1e-9f)
                {
                    slideAverage = slideAverage.normalized *
                        (rb.velocity.magnitude - (rb.velocity.magnitude - slideAverage.magnitude) / 2f);
                }

                rb.velocity = Vector3.zero;
            }
            else if (leftHandColliding || rightHandColliding)
            {
                rb.velocity = Vector3.zero;
            }

            // ---- jump / the launch ----
            if ((rightHandColliding || leftHandColliding) && !p.disableMovement && !p.didATurn)
            {
                if (p.rightHandSlide || (p.leftHandSlide &&
                        p.leftHandSlide == leftHandColliding && p.rightHandSlide == rightHandColliding))
                {
                    if ((denormalizedVelocityAverage - slideAverage).magnitude > p.slideVelocityLimit &&
                        Vector3.Dot(denormalizedVelocityAverage, slideAverageNormal) > 0f &&
                        denormalizedVelocityAverage.magnitude > slideAverage.magnitude)
                    {
                        p.leftHandSlide = false;
                        p.rightHandSlide = false;

                        if ((denormalizedVelocityAverage - slideAverage).magnitude > p.maxJumpSpeed)
                        {
                            rb.velocity = (denormalizedVelocityAverage - slideAverage).normalized * p.maxJumpSpeed +
                                          Vector3.ProjectOnPlane(slideAverage, slideAverageNormal);
                        }
                        else
                        {
                            Vector3 into = Vector3.Project(denormalizedVelocityAverage, slideAverageNormal);
                            rb.velocity = Vector3.ProjectOnPlane(denormalizedVelocityAverage, slideAverageNormal) +
                                          into.normalized * Mathf.Min(p.maxJumpSpeed, into.magnitude);
                        }

                        if (LogLaunches)
                            Debug.Log($"[SlipSlap] launch {rb.velocity.magnitude:F1} m/s dir {rb.velocity.normalized}");
                    }
                }
                else if (denormalizedVelocityAverage.magnitude > p.velocityLimit)
                {
                    rb.velocity = (denormalizedVelocityAverage.magnitude * p.jumpMultiplier > p.maxJumpSpeed)
                        ? denormalizedVelocityAverage.normalized * p.maxJumpSpeed
                        : p.jumpMultiplier * denormalizedVelocityAverage;
                }
            }

            // ---- unstick ----
            if (leftHandColliding &&
                (CurrentLeftHandPosition(p) - lastLeftHandPosition).magnitude > p.unStickDistance &&
                !Physics.SphereCast(p.headCollider.transform.position, p.minimumRaycastDistance * p.defaultPrecision,
                    CurrentLeftHandPosition(p) - p.headCollider.transform.position, out hitInfo,
                    (CurrentLeftHandPosition(p) - p.headCollider.transform.position).magnitude - p.minimumRaycastDistance,
                    p.locomotionEnabledLayers.value))
            {
                lastLeftHandPosition = CurrentLeftHandPosition(p);
                leftHandColliding = false;
            }

            if (rightHandColliding &&
                (CurrentRightHandPosition(p) - lastRightHandPosition).magnitude > p.unStickDistance &&
                !Physics.SphereCast(p.headCollider.transform.position, p.minimumRaycastDistance * p.defaultPrecision,
                    CurrentRightHandPosition(p) - p.headCollider.transform.position, out hitInfo,
                    (CurrentRightHandPosition(p) - p.headCollider.transform.position).magnitude - p.minimumRaycastDistance,
                    p.locomotionEnabledLayers.value))
            {
                lastRightHandPosition = CurrentRightHandPosition(p);
                rightHandColliding = false;
            }

            p.leftHandFollower.position = lastLeftHandPosition;
            p.rightHandFollower.position = lastRightHandPosition;
            p.wasLeftHandTouching = leftHandColliding;
            p.wasRightHandTouching = rightHandColliding;
            p.wasLeftHandSlide = p.leftHandSlide;
            p.wasRightHandSlide = p.rightHandSlide;

            // Mirror for anything else reading these.
            gameSlideAverageRef(p) = slideAverage;
            gameSlideNormalRef(p) = slideAverageNormal;
            denormRef(p) = denormalizedVelocityAverage;
            gameLeftCollidingRef(p) = leftHandColliding;
            gameRightCollidingRef(p) = rightHandColliding;

            // LateUpdate normally ends with this; we replaced the method.
            bodyCollider?.Invoke(p, null);

            return false;
        }

        // ---------------------------------------------------------
        static Vector3 SteerAlong(Vector3 slide, Vector3 surfaceDirection, float control)
        {
            if (slide.sqrMagnitude < 1e-9f || surfaceDirection.sqrMagnitude < 1e-9f) return slide;

            Vector3 target = (Vector3.Dot(slide, surfaceDirection) > 0f)
                ? surfaceDirection.normalized * slide.magnitude
                : -surfaceDirection.normalized * slide.magnitude;

            return Vector3.Project(slide, Vector3.Slerp(slide, target, control));
        }

        static Vector3 SafeBodyOffsetVector(Player p)
        {
            try { return bodyOffsetVectorRef(p); }
            catch { return Vector3.zero; }
        }

        static Vector3 PositionWithOffset(Transform t, Vector3 offset) => t.position + t.rotation * offset;

        static Vector3 CurrentLeftHandPosition(Player p)
        {
            if (p.inOverlay) return p.headCollider.transform.position + p.headCollider.transform.up * -0.5f;
            Vector3 target = PositionWithOffset(p.leftHandTransform, p.leftHandOffset);
            if ((target - p.headCollider.transform.position).magnitude < p.maxArmLength) return target;
            return p.headCollider.transform.position +
                   (target - p.headCollider.transform.position).normalized * p.maxArmLength;
        }

        static Vector3 CurrentRightHandPosition(Player p)
        {
            if (p.inOverlay) return p.headCollider.transform.position + p.headCollider.transform.up * -0.5f;
            Vector3 target = PositionWithOffset(p.rightHandTransform, p.rightHandOffset);
            if ((target - p.headCollider.transform.position).magnitude < p.maxArmLength) return target;
            return p.headCollider.transform.position +
                   (target - p.headCollider.transform.position).normalized * p.maxArmLength;
        }

        static void StoreVelocities(Player p)
        {
            int size = velocityHistory.Length;
            velocityIndex = (velocityIndex + 1) % size;
            currentVelocity = p.didATurn ? Vector3.zero : ((p.transform.position - lastPosition) / calcDeltaTime);
            velocityHistory[velocityIndex] = currentVelocity;

            denormalizedVelocityAverage = Vector3.zero;
            for (int i = 0; i < size; i++) denormalizedVelocityAverage += velocityHistory[i];
            denormalizedVelocityAverage /= size;

            lastPosition = p.transform.position;
            p.didATurn = false;
        }

        static void AntiTeleport(Player p, Rigidbody rb)
        {
            if ((p.headCollider.transform.position - lastHeadPosition).magnitude >=
                p.teleportThresholdNoVel + rb.velocity.magnitude * calcDeltaTime)
            {
                p.transform.position += lastHeadPosition - p.headCollider.transform.position;
            }
        }

        // ---- old 9-arg version, returns surfaceNormal ----------------
        static bool IterativeCollisionSphereCast(Player p, Vector3 startPosition, float sphereRadius,
            Vector3 movementVector, float precision, out Vector3 endPosition, bool singleHand,
            out float slipPercentage, out Vector3 surfaceNormal, out RaycastHit iterativeHitInfo)
        {
            slipPercentage = p.defaultSlideFactor;
            surfaceNormal = Vector3.up;
            iterativeHitInfo = default;

            if (CollisionsSphereCast(p, startPosition, sphereRadius * precision, movementVector, precision,
                    out endPosition, out RaycastHit tempIterativeHit))
            {
                Vector3 firstPosition = endPosition;
                iterativeHitInfo = tempIterativeHit;

                float slidePercentage = SlidePercentage(p, iterativeHitInfo);
                slipPercentage = (slidePercentage != p.defaultSlideFactor)
                    ? slidePercentage
                    : (singleHand ? 0.001f : p.defaultSlideFactor);
                surfaceNormal = iterativeHitInfo.normal;

                Vector3 projected = Vector3.ProjectOnPlane(
                    startPosition + movementVector - firstPosition, iterativeHitInfo.normal) * slipPercentage;

                if (CollisionsSphereCast(p, endPosition, sphereRadius, projected, precision * precision,
                        out endPosition, out tempIterativeHit))
                {
                    iterativeHitInfo = tempIterativeHit;
                    surfaceNormal = iterativeHitInfo.normal;
                    return true;
                }

                if (CollisionsSphereCast(p, projected + firstPosition, sphereRadius,
                        startPosition + movementVector - (projected + firstPosition),
                        precision * precision * precision, out endPosition, out tempIterativeHit))
                {
                    iterativeHitInfo = tempIterativeHit;
                    surfaceNormal = iterativeHitInfo.normal;
                    return true;
                }

                endPosition = firstPosition;
                return true;
            }

            if (CollisionsSphereCast(p, startPosition, sphereRadius * precision * 0.66f,
                    movementVector.normalized * (movementVector.magnitude + sphereRadius * precision * 0.34f),
                    precision * 0.66f, out endPosition, out iterativeHitInfo))
            {
                endPosition = startPosition;
                surfaceNormal = iterativeHitInfo.normal;
                return true;
            }

            endPosition = Vector3.zero;
            return false;
        }

        // ---- old 6-arg version, takes precision ----------------------
        static bool CollisionsSphereCast(Player p, Vector3 startPosition, float sphereRadius,
            Vector3 movementVector, float precision, out Vector3 finalPosition, out RaycastHit collisionsHitInfo)
        {
            int mask = p.locomotionEnabledLayers.value;

            if (Physics.SphereCast(startPosition, sphereRadius * precision, movementVector, out collisionsHitInfo,
                    movementVector.magnitude + sphereRadius * (1f - precision), mask))
            {
                finalPosition = collisionsHitInfo.point + collisionsHitInfo.normal * sphereRadius;

                if (Physics.SphereCast(startPosition, sphereRadius * precision * precision,
                        finalPosition - startPosition, out RaycastHit inner,
                        (finalPosition - startPosition).magnitude + sphereRadius * (1f - precision * precision), mask))
                {
                    finalPosition = startPosition + (finalPosition - startPosition).normalized *
                        Mathf.Max(0f, collisionsHitInfo.distance - sphereRadius * (1f - precision * precision));
                    collisionsHitInfo = inner;

                    if (Physics.Raycast(finalPosition, inner.point - finalPosition, out RaycastHit inner2,
                            (inner.point - finalPosition).magnitude * 1.05f, mask))
                        collisionsHitInfo = inner2;
                }
                else
                {
                    if (Physics.Raycast(startPosition, finalPosition - startPosition, out RaycastHit inner3,
                            (finalPosition - startPosition).magnitude + sphereRadius * precision * precision * 0.999f, mask))
                    {
                        finalPosition = startPosition;
                        collisionsHitInfo = inner3;
                        return true;
                    }
                    if (Physics.Raycast(finalPosition, collisionsHitInfo.point - finalPosition, out RaycastHit inner4,
                            (collisionsHitInfo.point - finalPosition).magnitude * 1.05f, mask))
                        collisionsHitInfo = inner4;
                }
                return true;
            }

            if (Physics.Raycast(startPosition, movementVector, out collisionsHitInfo,
                    movementVector.magnitude + sphereRadius * precision * 0.999f, mask))
            {
                finalPosition = startPosition;
                return true;
            }

            finalPosition = Vector3.zero;
            return false;
        }

        // Game's own lookup - also sets currentMaterialIndex/currentOverride.
        static float SlidePercentage(Player p, RaycastHit hit)
        {
            if (getSlidePercentage == null || hit.collider == null) return p.defaultSlideFactor;
            try { return (float)getSlidePercentage.Invoke(p, new object[] { hit }); }
            catch { return p.defaultSlideFactor; }
        }
    }
}
