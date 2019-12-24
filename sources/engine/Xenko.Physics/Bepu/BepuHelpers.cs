using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using Xenko.Core;
using Xenko.Core.Mathematics;
using Xenko.Engine;
using Xenko.Games;
using Xenko.Graphics;
using Xenko.Rendering.Rendering;

namespace Xenko.Physics.Bepu
{
    public class BepuHelpers
    {
        internal static PhysicsSystem physicsSystem;

        /// <summary>
        /// Good to call this at the start of your application. Will automatically get called in some situations, but not be soon enough.
        /// </summary>
        public static void AssureBepuSystemCreated()
        {
            if (physicsSystem == null)
            {
                physicsSystem = ServiceRegistry.instance.GetService<PhysicsSystem>();
                if (physicsSystem == null)
                {
                    physicsSystem = new PhysicsSystem(ServiceRegistry.instance);
                    ServiceRegistry.instance.AddService<IPhysicsSystem>(physicsSystem);
                    var gameSystems = ServiceRegistry.instance.GetSafeServiceAs<IGameSystemCollection>();
                    gameSystems.Add(physicsSystem);
                    ((IReferencable)physicsSystem).AddReference();
                    physicsSystem.Create(null, PhysicsEngineFlags.None, true);
                }
                else if (physicsSystem.HasSimulation<BepuSimulation>() == false)
                {
                    physicsSystem.Create(null, PhysicsEngineFlags.None, true);
                }
            }
        }

        private static Vector3 getBounds(Entity e)
        {
            ModelComponent mc = e.Get<ModelComponent>();
            if (mc == null || mc.Model == null || mc.Model.Meshes.Count < 0f) return Vector3.Zero;

            Vector3 biggest = Vector3.Zero;
            for (int i=0; i<mc.Model.Meshes.Count; i++)
            {
                Xenko.Rendering.Mesh m = mc.Model.Meshes[i];
                Vector3 extent = m.BoundingBox.Extent;
                if (extent.X > biggest.X) biggest.X = extent.X;
                if (extent.Y > biggest.Y) biggest.Y = extent.Y;
                if (extent.Z > biggest.Z) biggest.Z = extent.Z;
            }
            return biggest * e.Transform.WorldScale();
        }

        /// <summary>
        /// Is this an OK shape? Checks for 0 or negative sizes, or compounds with no children etc...
        /// </summary>
        /// <param name="shape">Shape to check</param>
        /// <returns>true is this shape is sane, false if it has problems</returns>
        public static bool SanityCheckShape(IShape shape)
        {
            if (shape is Box box)
                return box.HalfHeight > 0f && box.HalfLength > 0f && box.HalfWidth > 0f;

            if (shape is Sphere sphere)
                return sphere.Radius > 0f;

            if (shape is Cylinder cylinder)
                return cylinder.Radius > 0f && cylinder.HalfLength > 0f;

            if (shape is Capsule capsule)
                return capsule.HalfLength > 0f && capsule.Radius > 0f;

            if (shape is Triangle triangle)
                return triangle.A != triangle.B && triangle.A != triangle.C && triangle.B != triangle.C;

            if (shape is ICompoundShape compound)
                return compound.ChildCount > 0;

            if (shape is Mesh mesh)
                return mesh.ChildCount > 0;

            return shape != null;
        }

        public static IShape OffsetSingleShape(IConvexShape shape, Vector3? offset = null, Quaternion? rotation = null)
        {
            if (offset.HasValue == false && rotation.HasValue == false) return shape;

            if (shape is ICompoundShape) throw new InvalidOperationException("Cannot offset a compound shape. Can't support nested compounds.");

            using (var compoundBuilder = new CompoundBuilder(BepuSimulation.instance.pBufferPool, BepuSimulation.instance.internalSimulation.Shapes, 1))
            {
                compoundBuilder.AddForKinematicEasy(shape, new BepuPhysics.RigidPose(ToBepu(offset ?? Vector3.Zero), ToBepu(rotation ?? Quaternion.Identity)), 1f);

                compoundBuilder.BuildKinematicCompound(out var children);

                return new Compound(children);
            }
        }

        public static Box GenerateBoxOfEntity(Entity e, float scale = 1f)
        {
            Vector3 b = getBounds(e) * scale * 2f;
            return new Box(b.X, b.Y, b.Z);
        }

        public static Sphere GenerateSphereOfEntity(Entity e, float scale = 1f)
        {
            Vector3 b = getBounds(e);
            return new Sphere(Math.Max(b.Z, Math.Max(b.X, b.Y)) * scale);
        }

        public static Capsule GenerateCapsuleOfEntity(Entity e, float scale = 1f, bool XZradius = true)
        {
            Vector3 b = getBounds(e) * scale;
            return XZradius ? new Capsule(Math.Max(b.X, b.Z), b.Y * 2f) : new Capsule(b.Y, 2f * Math.Max(b.X, b.Z));
        }

        public static Cylinder GenerateCylinderOfEntity(Entity e, float scale = 1f, bool XZradius = true)
        {
            Vector3 b = getBounds(e) * scale;
            return XZradius ? new Cylinder(Math.Max(b.X, b.Z), b.Y * 2f) : new Cylinder(b.Y, 2f * Math.Max(b.X, b.Z));
        }

        /// <summary>
        /// Since you can't have non-convex shapes (e.g. mesh's) in a compound object, this helper will generate a bunch of individual static components to attach to an entity, with each shape.
        /// </summary>
        /// <param name="e">Entity to add static components to</param>
        /// <param name="shapes">shapes that will generate a static component for each</param>
        /// <param name="offsets">optional offset for each</param>
        /// <param name="rotations">optional rotation for each</param>
        public static void GenerateStaticComponents(Entity e, List<IShape> shapes, List<Vector3> offsets = null, List<Quaternion> rotations = null,
                                                    CollisionFilterGroups group = CollisionFilterGroups.DefaultFilter, CollisionFilterGroupFlags collidesWith = CollisionFilterGroupFlags.AllFilter,
                                                    float FrictionCoefficient = 0.5f, float MaximumRecoverableVelocity = 3f, SpringSettings? springSettings = null)
        {
            for (int i=0; i<shapes.Count; i++)
            {
                BepuStaticColliderComponent sc = new BepuStaticColliderComponent();
                sc.ColliderShape = shapes[i];
                sc.Position = offsets?[i] ?? Vector3.Zero;
                sc.Rotation = rotations?[i] ?? Quaternion.Identity;
                sc.CanCollideWith = collidesWith;
                sc.CollisionGroup = group;
                sc.FrictionCoefficient = FrictionCoefficient;
                sc.MaximumRecoveryVelocity = MaximumRecoverableVelocity;
                if (springSettings.HasValue) sc.SpringSettings = springSettings.Value;
                e.Add(sc);
            }
        }

        /// <summary>
        /// Disposes of all mesh buffers used on an entity, of all static colliders
        /// </summary>
        /// <param name="e">Entity to dispose of static mesh colliders</param>
        public static void DisposeAllStaticMeshes(Entity e)
        {
            foreach(BepuStaticColliderComponent sc in e.GetAll<BepuStaticColliderComponent>())
            {
                if (sc.ColliderShape is Mesh m)
                {
                    sc.AddedToScene = false;
                    m.Dispose(BepuSimulation.instance.pBufferPool);
                    sc.ColliderShape = null;
                }
            }
        }

        /// <summary>
        /// Easily makes a Compound shape for you, given a list of individual shapes and how they should be offset.
        /// </summary>
        /// <param name="shapes">List of convex shapes</param>
        /// <param name="offsets">Matching length list of offsets of bodies, can be null if nothing has an offset</param>
        /// <param name="rotations">Matching length list of rotations of bodies, can be null if nothing is rotated</param>
        /// <param name="isDynamic">True if intended to use in a dynamic situation, false if kinematic or static</param>
        /// <returns></returns>
        public static ICompoundShape MakeCompound(List<IConvexShape> shapes, List<Vector3> offsets = null, List<Quaternion> rotations = null, bool isDynamic = true, int bigThreshold = 5)
        {
            using (var compoundBuilder = new CompoundBuilder(BepuSimulation.instance.pBufferPool, BepuSimulation.instance.internalSimulation.Shapes, shapes.Count))
            {
                bool allConvex = true;

                //All allocations from the buffer pool used for the final compound shape will be disposed when the demo is disposed. Don't have to worry about leaks in these demos.
                for (int i=0; i<shapes.Count; i++)
                {
                    if (shapes[i] is ICompoundShape) throw new InvalidOperationException("Cannot include compounds in another compound shape.");

                    if (isDynamic)
                    {
                        compoundBuilder.AddEasy(shapes[i] as IConvexShape, new BepuPhysics.RigidPose(ToBepu(offsets?[i] ?? Vector3.Zero), ToBepu(rotations?[i] ?? Quaternion.Identity)), 1f);
                    } 
                    else
                    {
                        if (shapes[i] is IConvexShape == false) allConvex = false;

                        compoundBuilder.AddForKinematicEasy(shapes[i], new BepuPhysics.RigidPose(ToBepu(offsets?[i] ?? Vector3.Zero), ToBepu(rotations?[i] ?? Quaternion.Identity)), 1f);
                    }
                }

                return compoundBuilder.BuildCompleteCompoundShape(BepuSimulation.instance.internalSimulation.Shapes, BepuSimulation.instance.pBufferPool, isDynamic, allConvex ? bigThreshold : int.MaxValue);
            }
        }

        /// <summary>
        /// Goes through the whole scene and adds bepu physics objects to the simulation. Only will add if AllowHelperToAdd is true (which is set to true by default)
        /// and if the body isn't added already.
        /// </summary>
        /// <param name="rootScene"></param>
        public static void SetBodiesInSimulation(Scene rootScene, bool add = true)
        {
            foreach (Entity e in rootScene.Entities)
                SetBodiesInSimulation(e, add);
        }

        /// <summary>
        /// Goes through the entity and children and adds/removes bepu physics objects to the simulation. Only will add/remove if AllowHelperToManage is true (which is set to true by default)
        /// and if the body isn't added already.
        /// </summary>
        /// <param name="rootEntity"></param>
        public static void SetBodiesInSimulation(Entity rootEntity, bool add = true)
        {
            foreach (BepuPhysicsComponent pc in rootEntity.GetAll<BepuPhysicsComponent>())
                if (pc.AutomaticAdd) pc.AddedToScene = add;
            foreach (Entity e in rootEntity.GetChildren())
                SetBodiesInSimulation(e, add);
        }

        /// <summary>
        /// Shortcut to clearing the simulation of all bodies. Optionally clears all the buffers too (e.g. mesh colliders), which is enabled by default
        /// </summary>
        public static void ClearSimulation(bool clearBuffers = true)
        {
            BepuSimulation.instance.Clear(clearBuffers);
        }

        /// <summary>
        /// Generate a mesh collider from a given mesh. The mesh must have a readable buffer behind it to generate veriticies from
        /// </summary>
        /// <returns>Returns false if no mesh could be made</returns>
        public static unsafe bool GenerateMeshShape(Xenko.Rendering.Mesh modelMesh, out BepuPhysics.Collidables.Mesh outMesh, Vector3? scale = null)
        {
            List<Vector3> positions;
            List<int> indicies;

            if (modelMesh.Draw is StagedMeshDraw)
            {
                StagedMeshDraw smd = modelMesh.Draw as StagedMeshDraw;

                object verts = smd.Verticies;

                if (verts is VertexPositionNormalColor[])
                {
                    VertexPositionNormalColor[] vpnc = verts as VertexPositionNormalColor[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions[k] = vpnc[k].Position;
                }
                else if (verts is VertexPositionNormalTexture[])
                {
                    VertexPositionNormalTexture[] vpnc = verts as VertexPositionNormalTexture[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions[k] = vpnc[k].Position;
                }
                else if (verts is VertexPositionNormalTextureTangent[])
                {
                    VertexPositionNormalTextureTangent[] vpnc = verts as VertexPositionNormalTextureTangent[];
                    positions = new List<Vector3>(vpnc.Length);
                    for (int k = 0; k < vpnc.Length; k++)
                        positions[k] = vpnc[k].Position;
                }
                else
                {
                    outMesh = new Mesh();
                    return false;
                }

                // take care of indicies
                indicies = new List<int>((int[])(object)smd.Indicies);
            }
            else
            {
                Xenko.Graphics.Buffer buf = modelMesh.Draw?.VertexBuffers[0].Buffer;
                Xenko.Graphics.Buffer ibuf = modelMesh.Draw?.IndexBuffer.Buffer;
                if (buf == null || buf.VertIndexData == null ||
                    ibuf == null || ibuf.VertIndexData == null)
                {
                    outMesh = new Mesh();
                    return false;
                }

                if (ModelBatcher.UnpackRawVertData(buf.VertIndexData, modelMesh.Draw.VertexBuffers[0].Declaration,
                                                   out Vector3[] arraypositions, out Core.Mathematics.Vector3[] normals, out Core.Mathematics.Vector2[] uvs,
                                                   out Color4[] colors, out Vector4[] tangents) == false)
                {
                    outMesh = new Mesh();
                    return false;
                }

                // indicies
                fixed (byte* pdst = ibuf.VertIndexData)
                {
                    if (modelMesh.Draw.IndexBuffer.Is32Bit)
                    {
                        var dst = (uint*)pdst;

                        int numIndices = ibuf.VertIndexData.Length / sizeof(uint);
                        indicies = new List<int>(numIndices);
                        for (var k = 0; k < numIndices; k++)
                        {
                            // Offset indices
                            indicies[k] = (int)dst[k];
                        }
                    }
                    else
                    {
                        var dst = (ushort*)pdst;

                        int numIndices = ibuf.VertIndexData.Length / sizeof(ushort);
                        indicies = new List<int>(numIndices);
                        for (var k = 0; k < numIndices; k++)
                        {
                            // Offset indices
                            indicies[k] = dst[k];
                        }
                    }
                }

                // take care of positions
                positions = new List<Vector3>(arraypositions);
            }

            return GenerateMeshShape(positions, indicies, out outMesh, scale);
        }

        public static unsafe bool GenerateMeshShape(List<Vector3> positions, List<int> indicies, out BepuPhysics.Collidables.Mesh outMesh, Vector3? scale = null)
        {
            // ok, should have what we need to make triangles
            int triangleCount = indicies.Count / 3;
            BepuSimulation.instance.pBufferPool.Take<Triangle>(triangleCount, out BepuUtilities.Memory.Buffer<Triangle> triangles);

            for (int i = 0; i < triangleCount; i ++)
            {
                int shiftedi = i * 3;
                triangles[i].A = ToBepu(positions[indicies[shiftedi]]);
                triangles[i].B = ToBepu(positions[indicies[shiftedi+1]]);
                triangles[i].C = ToBepu(positions[indicies[shiftedi+2]]);
            }

            outMesh = new Mesh(triangles, new System.Numerics.Vector3(scale?.X ?? 1f, scale?.Y ?? 1f, scale?.Z ?? 1f), BepuSimulation.instance.pBufferPool);
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe System.Numerics.Vector3 ToBepu(Xenko.Core.Mathematics.Vector3 v)
        {
            return *((System.Numerics.Vector3*)(void*)&v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Xenko.Core.Mathematics.Vector3 ToXenko(System.Numerics.Vector3 v)
        {
            return *((Xenko.Core.Mathematics.Vector3*)(void*)&v);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe Xenko.Core.Mathematics.Quaternion ToXenko(BepuUtilities.Quaternion q)
        {
            return *((Xenko.Core.Mathematics.Quaternion*)(void*)&q);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe BepuUtilities.Quaternion ToBepu(Xenko.Core.Mathematics.Quaternion q)
        {
            return *((BepuUtilities.Quaternion*)(void*)&q);
        }
    }
}
