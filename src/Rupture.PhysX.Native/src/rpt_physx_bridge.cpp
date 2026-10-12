#include "rpt_physx_bridge.h"

#include "PxPhysicsAPI.h"
#include "extensions/PxD6Joint.h"
#include "extensions/PxDefaultCpuDispatcher.h"
#include "extensions/PxDefaultStreams.h"
#include "extensions/PxDistanceJoint.h"
#include "extensions/PxExtensionsAPI.h"
#include "extensions/PxFixedJoint.h"

#include <algorithm>
#include <cmath>
#include <limits>
#include <memory>
#include <mutex>
#include <string>
#include <unordered_set>
#include <unordered_map>
#include <utility>
#include <vector>
#include <deque>

using namespace physx;

namespace
{
thread_local std::string g_last_error;

int32_t fail(RptPxResult result, std::string message)
{
    g_last_error = std::move(message);
    return result;
}

bool finite(float value) { return std::isfinite(value); }
bool finite(const RptPxVec3& value) { return finite(value.x) && finite(value.y) && finite(value.z); }
bool finite(const RptPxQuat& value) { return finite(value.x) && finite(value.y) && finite(value.z) && finite(value.w); }

PxVec3 to_px(const RptPxVec3& value) { return PxVec3(value.x, value.y, value.z); }
PxQuat to_px(const RptPxQuat& value)
{
    PxQuat result(value.x, value.y, value.z, value.w);
    return result.magnitudeSquared() > 0.000001f ? result.getNormalized() : PxQuat(PxIdentity);
}
PxTransform to_px(const RptPxPose& value) { return PxTransform(to_px(value.position), to_px(value.rotation)); }
RptPxVec3 from_px(const PxVec3& value) { return { value.x, value.y, value.z }; }
RptPxQuat from_px(const PxQuat& value) { return { value.x, value.y, value.z, value.w }; }
RptPxPose from_px(const PxTransform& value) { return { from_px(value.p), from_px(value.q) }; }

constexpr uint32_t source_scene_flag_mask()
{
    return static_cast<uint32_t>(PxSceneFlag::eENABLE_CCD) |
        static_cast<uint32_t>(PxSceneFlag::eDISABLE_CCD_RESWEEP) |
        static_cast<uint32_t>(PxSceneFlag::eADAPTIVE_FORCE) |
        static_cast<uint32_t>(PxSceneFlag::eENABLE_PCM) |
        static_cast<uint32_t>(PxSceneFlag::eDISABLE_CONTACT_CACHE) |
        static_cast<uint32_t>(PxSceneFlag::eENABLE_STABILIZATION) |
        static_cast<uint32_t>(PxSceneFlag::eENABLE_AVERAGE_POINT) |
        static_cast<uint32_t>(PxSceneFlag::eENABLE_ENHANCED_DETERMINISM) |
        static_cast<uint32_t>(PxSceneFlag::eENABLE_FRICTION_EVERY_ITERATION);
}

void copy_scene_flag(PxSceneFlags& target, uint32_t source, PxSceneFlag::Enum flag)
{
    if ((source & static_cast<uint32_t>(flag)) != 0)
        target.set(flag);
    else
        target.clear(flag);
}

void copy_source_scene_flags(PxSceneFlags& target, uint32_t source)
{
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_CCD);
    copy_scene_flag(target, source, PxSceneFlag::eDISABLE_CCD_RESWEEP);
    copy_scene_flag(target, source, PxSceneFlag::eADAPTIVE_FORCE);
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_PCM);
    copy_scene_flag(target, source, PxSceneFlag::eDISABLE_CONTACT_CACHE);
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_STABILIZATION);
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_AVERAGE_POINT);
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_ENHANCED_DETERMINISM);
    copy_scene_flag(target, source, PxSceneFlag::eENABLE_FRICTION_EVERY_ITERATION);
}

class ErrorCallback final : public PxErrorCallback
{
public:
    void reportError(PxErrorCode::Enum code, const char* message, const char* file, int line) override
    {
        if (code == PxErrorCode::eDEBUG_INFO || code == PxErrorCode::eDEBUG_WARNING)
            return;
        g_last_error = std::string(message ? message : "PhysX error") + " (" + (file ? file : "unknown") + ":" + std::to_string(line) + ")";
    }
};

class ImmediateDispatcher final : public PxCpuDispatcher
{
public:
    void submitTask(PxBaseTask& task) override
    {
        task.run();
        task.release();
    }
    uint32_t getWorkerCount() const override { return 0; }
};

template<typename T>
class HandleStore
{
    struct Slot
    {
        T* value{};
        uint32_t generation{ 1 };
    };

public:
    uint64_t add(T* value)
    {
        if (!free_.empty())
        {
            const uint32_t index = free_.back();
            free_.pop_back();
            slots_[index].value = value;
            const uint64_t handle = make_handle(index, slots_[index].generation);
            handles_[value] = handle;
            return handle;
        }
        slots_.push_back({ value, 1 });
        const uint64_t handle = make_handle(static_cast<uint32_t>(slots_.size() - 1), 1);
        handles_[value] = handle;
        return handle;
    }

    T* get(uint64_t handle) const
    {
        if (!handle)
            return nullptr;
        const uint32_t encoded_index = static_cast<uint32_t>(handle);
        const uint32_t generation = static_cast<uint32_t>(handle >> 32);
        if (!encoded_index)
            return nullptr;
        const uint32_t index = encoded_index - 1;
        return index < slots_.size() && slots_[index].generation == generation ? slots_[index].value : nullptr;
    }

    T* remove(uint64_t handle)
    {
        if (!handle)
            return nullptr;
        const uint32_t encoded_index = static_cast<uint32_t>(handle);
        const uint32_t generation = static_cast<uint32_t>(handle >> 32);
        if (!encoded_index)
            return nullptr;
        const uint32_t index = encoded_index - 1;
        if (index >= slots_.size() || slots_[index].generation != generation || !slots_[index].value)
            return nullptr;
        T* value = slots_[index].value;
        slots_[index].value = nullptr;
        ++slots_[index].generation;
        if (!slots_[index].generation)
            slots_[index].generation = 1;
        handles_.erase(value);
        free_.push_back(index);
        return value;
    }

    template<typename F>
    void each(F&& callback)
    {
        for (Slot& slot : slots_)
            if (slot.value)
                callback(slot.value);
    }

    template<typename F>
    void each_handle(F&& callback)
    {
        for (uint32_t index = 0; index < slots_.size(); ++index)
            if (slots_[index].value)
                callback(make_handle(index, slots_[index].generation), slots_[index].value);
    }

    bool remove_value(T* value)
    {
        const auto found = handles_.find(value);
        return found != handles_.end() && remove(found->second) != nullptr;
    }

    void clear() { slots_.clear(); free_.clear(); handles_.clear(); }

private:
    static uint64_t make_handle(uint32_t index, uint32_t generation)
    {
        return (static_cast<uint64_t>(generation) << 32) | static_cast<uint64_t>(index + 1);
    }

    std::vector<Slot> slots_;
    std::vector<uint32_t> free_;
    std::unordered_map<T*, uint64_t> handles_;
};

struct MeshResource
{
    bool convex{};
    PxTriangleMesh* triangle{};
    PxConvexMesh* convex_mesh{};

    void release()
    {
        if (triangle) triangle->release();
        if (convex_mesh) convex_mesh->release();
        triangle = nullptr;
        convex_mesh = nullptr;
    }
};

uint64_t filter_pair_key(uint32_t left, uint32_t right)
{
    const uint32_t low = std::min(left, right);
    const uint32_t high = std::max(left, right);
    return (static_cast<uint64_t>(low) << 32) | high;
}

class PairFilterCallback final : public PxSimulationFilterCallback
{
public:
    std::unordered_set<uint64_t> ignored_pairs;

    PxFilterFlags pairFound(PxU32, PxFilterObjectAttributes, PxFilterData filter0, const PxActor*, const PxShape*,
        PxFilterObjectAttributes, PxFilterData filter1, const PxActor*, const PxShape*, PxPairFlags& pair_flags) override
    {
        if (ignored_pairs.find(filter_pair_key(filter0.word3, filter1.word3)) != ignored_pairs.end())
            return PxFilterFlag::eSUPPRESS;
        // Preserve the shader's CCD request; this callback only adds pair exclusions.
        return PxFilterFlag::eDEFAULT;
    }

    void pairLost(PxU32, PxFilterObjectAttributes, PxFilterData, PxFilterObjectAttributes, PxFilterData, bool) override {}
    bool statusChange(PxU32&, PxPairFlags&, PxFilterFlags&) override { return false; }
};

PxFilterFlags filter_shader(
    PxFilterObjectAttributes attributes0,
    PxFilterData filter0,
    PxFilterObjectAttributes attributes1,
    PxFilterData filter1,
    PxPairFlags& pair_flags,
    const void*,
    PxU32)
{
    if (PxFilterObjectIsTrigger(attributes0) || PxFilterObjectIsTrigger(attributes1))
        return PxFilterFlag::eSUPPRESS;
    if ((filter0.word0 & filter1.word1) == 0 || (filter1.word0 & filter0.word1) == 0)
        return PxFilterFlag::eSUPPRESS;
	// Scene-level and actor-level CCD only opt PhysX into the CCD pipeline. The
	// pair must also request swept contact generation or small, fast gore shapes
	// can still pass through map triangles between rendered steps.
    pair_flags = PxPairFlag::eCONTACT_DEFAULT | PxPairFlag::eDETECT_DISCRETE_CONTACT | PxPairFlag::eDETECT_CCD_CONTACT;
	return PxFilterFlag::eCALLBACK;
}

#include "rpt_casings.inl"

struct SceneContext
{
    PxDefaultAllocator allocator;
    ErrorCallback errors;
    PxFoundation* foundation{};
    PxPhysics* physics{};
    PxCooking* cooking{};
    PxScene* scene{};
    std::unique_ptr<CasingWorld> casings;
    PxCpuDispatcher* dispatcher{};
    ImmediateDispatcher immediate_dispatcher;
    PairFilterCallback filter_callback;
    bool owns_dispatcher{};
    std::mutex gate;
    bool simulating{};
    HandleStore<PxMaterial> materials;
    HandleStore<MeshResource> meshes;
    HandleStore<PxHeightField> heightfields;
    HandleStore<PxRigidActor> actors;
	std::unordered_set<PxRigidDynamic*> ccd_actors;
    HandleStore<PxShape> shapes;
    HandleStore<PxJoint> joints;
    uint32_t next_filter_shape_id{ 1 };
    std::vector<std::unique_ptr<MeshResource>> mesh_owners;
    std::vector<PxHeightField*> heightfield_owners;
    // One explicitly consumed cooking result per scene-owner transaction.
    std::vector<uint8_t> pending_cooked_mesh;

    ~SceneContext()
    {
        casings.reset();
        joints.each([](PxJoint* joint) { joint->release(); });
        actors.each([](PxRigidActor* actor) { actor->release(); });
        shapes.clear();
        materials.each([](PxMaterial* material) { material->release(); });
        for (auto& mesh : mesh_owners)
            mesh->release();
        for (PxHeightField* heightfield : heightfield_owners)
            heightfield->release();
        if (scene) scene->release();
        if (owns_dispatcher && dispatcher) static_cast<PxDefaultCpuDispatcher*>(dispatcher)->release();
        if (cooking) cooking->release();
        PxCloseExtensions();
        if (physics) physics->release();
        if (foundation) foundation->release();
    }
};

template<typename T>
bool valid_desc(const T* desc)
{
    return desc && desc->struct_size == sizeof(T);
}

SceneContext* context(void* scene) { return static_cast<SceneContext*>(scene); }

PxCombineMode::Enum combine_mode(uint32_t value)
{
    return value <= PxCombineMode::eMAX ? static_cast<PxCombineMode::Enum>(value) : PxCombineMode::eAVERAGE;
}

PxForceMode::Enum force_mode(uint32_t value)
{
    switch (value)
    {
    case RPTPX_IMPULSE: return PxForceMode::eIMPULSE;
    case RPTPX_VELOCITY_CHANGE: return PxForceMode::eVELOCITY_CHANGE;
    case RPTPX_ACCELERATION: return PxForceMode::eACCELERATION;
    default: return PxForceMode::eFORCE;
    }
}

void configure_cooking(PxCookingParams& params, uint32_t flags)
{
    // Matches Unity's public MeshColliderCookingOptions bits.
    if ((flags & 4u) == 0) params.meshPreprocessParams |= PxMeshPreprocessingFlag::eDISABLE_CLEAN_MESH;
    if ((flags & 8u) != 0) params.meshPreprocessParams |= PxMeshPreprocessingFlag::eWELD_VERTICES;
	if ((flags & 16u) != 0)
		params.midphaseDesc = PxMeshMidPhase::eBVH34;
	else
	{
		params.midphaseDesc = PxMeshMidPhase::eBVH33;
		params.midphaseDesc.mBVH33Desc.meshCookingHint = (flags & 2u) != 0 ? PxMeshCookingHint::eSIM_PERFORMANCE : PxMeshCookingHint::eCOOKING_PERFORMANCE;
	}
    params.meshWeldTolerance = 0.0001f;
}

bool cook_mesh(SceneContext& ctx, const RptPxMeshDesc& desc, PxDefaultMemoryOutputStream& output)
{
    PxCookingParams params(ctx.physics->getTolerancesScale());
    configure_cooking(params, desc.cooking_flags);
    ctx.cooking->setParams(params);
    if (desc.convex)
    {
        PxConvexMeshDesc convex;
        convex.points.count = desc.vertex_count;
        convex.points.stride = sizeof(RptPxVec3);
        convex.points.data = desc.vertices;
		// Unity's legacy InflateConvexMesh bit is retained in the cache identity,
		// but this pinned PhysX 4.1 branch has removed eINFLATE_CONVEX itself.
        convex.flags = PxConvexFlag::eCOMPUTE_CONVEX;
		convex.vertexLimit = 255;
        return ctx.cooking->cookConvexMesh(convex, output);
    }
    PxTriangleMeshDesc triangle;
    triangle.points.count = desc.vertex_count;
    triangle.points.stride = sizeof(RptPxVec3);
    triangle.points.data = desc.vertices;
    triangle.triangles.count = desc.index_count / 3;
    triangle.triangles.stride = sizeof(uint32_t) * 3;
    triangle.triangles.data = desc.indices;
    return ctx.cooking->cookTriangleMesh(triangle, output);
}

MeshResource* create_mesh_resource(SceneContext& ctx, bool convex, const uint8_t* data, uint32_t length)
{
    PxDefaultMemoryInputData input(const_cast<uint8_t*>(data), length);
    auto mesh = std::make_unique<MeshResource>();
    mesh->convex = convex;
    if (convex)
        mesh->convex_mesh = ctx.physics->createConvexMesh(input);
    else
        mesh->triangle = ctx.physics->createTriangleMesh(input);
    if ((!convex && !mesh->triangle) || (convex && !mesh->convex_mesh))
        return nullptr;
    MeshResource* value = mesh.get();
    ctx.mesh_owners.push_back(std::move(mesh));
    return value;
}

PxGeometryHolder geometry_for(SceneContext& ctx, const RptPxShapeDesc& desc)
{
    PxGeometryHolder geometry;
    const PxVec3 dimensions = to_px(desc.dimensions);
    switch (desc.geometry_kind)
    {
    case RPTPX_GEOMETRY_BOX:
        geometry.storeAny(PxBoxGeometry(std::max(0.0001f, dimensions.x), std::max(0.0001f, dimensions.y), std::max(0.0001f, dimensions.z)));
        break;
    case RPTPX_GEOMETRY_SPHERE:
        geometry.storeAny(PxSphereGeometry(std::max(0.0001f, dimensions.x)));
        break;
    case RPTPX_GEOMETRY_CAPSULE:
        geometry.storeAny(PxCapsuleGeometry(std::max(0.0001f, dimensions.x), std::max(0.0001f, dimensions.y)));
        break;
    case RPTPX_GEOMETRY_TRIANGLE_MESH:
    case RPTPX_GEOMETRY_CONVEX_MESH:
    {
        MeshResource* mesh = ctx.meshes.get(desc.mesh_handle);
        if (!mesh) break;
        const PxMeshScale scale(to_px(desc.mesh_scale), PxQuat(PxIdentity));
        if (desc.geometry_kind == RPTPX_GEOMETRY_CONVEX_MESH && mesh->convex_mesh)
            geometry.storeAny(PxConvexMeshGeometry(mesh->convex_mesh, scale));
        else if (desc.geometry_kind == RPTPX_GEOMETRY_TRIANGLE_MESH && mesh->triangle)
            geometry.storeAny(PxTriangleMeshGeometry(mesh->triangle, scale));
        break;
    }
    case RPTPX_GEOMETRY_HEIGHTFIELD:
    {
        PxHeightField* heightfield = ctx.heightfields.get(desc.mesh_handle);
        if (heightfield)
        {
            const PxMeshGeometryFlags flags(static_cast<PxU8>(desc.geometry_flags));
            geometry.storeAny(PxHeightFieldGeometry(heightfield, flags, dimensions.x, dimensions.y, dimensions.z));
        }
        break;
    }
    default:
        break;
    }
    return geometry;
}

PxRigidActor* actor_for_joint(SceneContext& ctx, uint64_t handle)
{
    return handle == 0 ? nullptr : ctx.actors.get(handle);
}

void configure_joint(PxJoint& joint, float break_force, float break_torque, bool projection,
    float mass_scale, float connected_mass_scale, bool enable_preprocessing)
{
    joint.setBreakForce(break_force > 0.f ? break_force : PX_MAX_F32, break_torque > 0.f ? break_torque : PX_MAX_F32);
    joint.setConstraintFlag(PxConstraintFlag::ePROJECTION, projection);
	joint.setConstraintFlag(PxConstraintFlag::eDISABLE_PREPROCESSING, !enable_preprocessing);
	joint.setInvMassScale0(std::max(0.f, mass_scale));
	joint.setInvInertiaScale0(std::max(0.f, mass_scale));
	joint.setInvMassScale1(std::max(0.f, connected_mass_scale));
	joint.setInvInertiaScale1(std::max(0.f, connected_mass_scale));
}

int32_t attach_shape_locked(SceneContext& ctx, PxRigidActor* actor, const RptPxShapeDesc& desc,
    PxMaterial*const* materials, PxU16 material_count, uint64_t* output)
{
    if (!actor || !materials || material_count == 0)
        return fail(RPTPX_INVALID_HANDLE, "Actor or material handles are stale");
    for (PxU16 index = 0; index < material_count; ++index)
        if (!materials[index]) return fail(RPTPX_INVALID_HANDLE, "Actor or material handles are stale");
    PxGeometryHolder geometry = geometry_for(ctx, desc);
    if (geometry.getType() == PxGeometryType::eINVALID)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid shape geometry or geometry-resource handle");
    PxShape* shape = ctx.physics->createShape(geometry.any(), materials, material_count, true);
    if (!shape) return fail(RPTPX_PHYSX_ERROR, "Shape creation failed");
    shape->setLocalPose(to_px(desc.local_pose));
    shape->setContactOffset(std::max(0.0001f, desc.contact_offset));
    uint32_t filter_shape_id = ctx.next_filter_shape_id++;
    if (!ctx.next_filter_shape_id) ctx.next_filter_shape_id = 1;
    const PxFilterData filter(desc.layer_bits, desc.collides_with_bits, desc.owner_group, filter_shape_id);
    shape->setSimulationFilterData(filter);
    shape->setQueryFilterData(filter);
    shape->setFlag(PxShapeFlag::eSIMULATION_SHAPE, desc.enabled != 0);
    shape->setFlag(PxShapeFlag::eSCENE_QUERY_SHAPE, ctx.casings && desc.enabled != 0);
    if (!actor->attachShape(*shape))
    {
        shape->release();
        return fail(RPTPX_PHYSX_ERROR, "Actor rejected shape");
    }
    *output = ctx.shapes.add(shape);
    shape->release();
    g_last_error.clear();
    return RPTPX_OK;
}
}

int32_t RPTPX_CALL rptpx_get_abi(uint32_t* abi_version, uint32_t* physx_version)
{
    if (!abi_version || !physx_version) return fail(RPTPX_INVALID_ARGUMENT, "ABI output pointers are required");
    *abi_version = RPTPX_ABI_VERSION;
    *physx_version = PX_PHYSICS_VERSION;
    return RPTPX_OK;
}

const char* RPTPX_CALL rptpx_get_last_error() { return g_last_error.c_str(); }

int32_t RPTPX_CALL rptpx_unity_get_scene_from_actor(void* actor_pointer, void** unity_scene)
{
    if (!actor_pointer || !unity_scene)
        return fail(RPTPX_INVALID_ARGUMENT, "Unity actor and scene output pointers are required");
    PxActor* actor = static_cast<PxActor*>(actor_pointer);
    PxScene* scene = actor->getScene();
    if (!scene)
        return fail(RPTPX_INVALID_HANDLE, "The Unity PhysX actor is not attached to a scene");
    *unity_scene = scene;
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_unity_get_scene_settings(void* unity_scene, RptPxUnitySceneSettings* settings)
{
    if (!unity_scene || !valid_desc(settings))
        return fail(RPTPX_INVALID_ARGUMENT, "Unity scene and settings output are required");
    PxScene* scene = static_cast<PxScene*>(unity_scene);
    // PhysX 4.1 exposes friction and flags on PxScene but has no solver-type
    // getter. The exact supported EFT 2022.3.43f1 player serializes solver type
    // 0 (PGS), so keep that version-gated source value explicit in ABI 6.
    settings->solver_type = static_cast<uint32_t>(PxSolverType::ePGS);
    settings->friction_type = static_cast<uint32_t>(scene->getFrictionType());
    settings->scene_flags = static_cast<uint32_t>(scene->getFlags()) & source_scene_flag_mask();
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_unity_enumerate_shapes(void* unity_scene, void** shapes, uint32_t capacity, uint32_t* written_or_required)
{
    if (!unity_scene || !written_or_required || (!shapes && capacity != 0))
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid Unity PhysX scene enumeration arguments");

    PxScene* scene = static_cast<PxScene*>(unity_scene);
    const PxActorTypeFlags actor_types = PxActorTypeFlag::eRIGID_STATIC | PxActorTypeFlag::eRIGID_DYNAMIC;
    const PxU32 actor_capacity = scene->getNbActors(actor_types);
    std::vector<PxActor*> actors(actor_capacity);
    const PxU32 actor_count = actor_capacity ? scene->getActors(actor_types, actors.data(), actor_capacity) : 0;

    uint64_t required = 0;
    for (PxU32 index = 0; index < actor_count; ++index)
    {
        PxActor* actor = actors[index];
        if (!actor)
            continue;
        const PxActorType::Enum type = actor->getType();
        if (type != PxActorType::eRIGID_STATIC && type != PxActorType::eRIGID_DYNAMIC)
            continue;
        required += static_cast<PxRigidActor*>(actor)->getNbShapes();
        if (required > std::numeric_limits<uint32_t>::max())
            return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX scene contains too many shapes");
    }

    *written_or_required = static_cast<uint32_t>(required);
    if (required > capacity)
        return fail(RPTPX_BUFFER_TOO_SMALL, "Unity PhysX shape output buffer is too small");
    if (!required)
    {
        g_last_error.clear();
        return RPTPX_OK;
    }

    uint32_t written = 0;
    for (PxU32 index = 0; index < actor_count; ++index)
    {
        PxActor* actor = actors[index];
        if (!actor)
            continue;
        const PxActorType::Enum type = actor->getType();
        if (type != PxActorType::eRIGID_STATIC && type != PxActorType::eRIGID_DYNAMIC)
            continue;
        PxRigidActor* rigid = static_cast<PxRigidActor*>(actor);
        const PxU32 shape_count = rigid->getNbShapes();
        if (shape_count)
            written += rigid->getShapes(reinterpret_cast<PxShape**>(shapes + written), shape_count);
    }
    *written_or_required = written;
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_unity_overlap_shapes(void* unity_scene, const RptPxVec3* point0, const RptPxVec3* point1,
    float radius, void** shapes, uint32_t capacity, uint32_t* written_or_required)
{
    if (!unity_scene || !point0 || !point1 || !written_or_required || (!shapes && capacity != 0) ||
        !finite(*point0) || !finite(*point1) || !finite(radius) || radius <= 0.f)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid Unity PhysX overlap arguments");

    class CollectOverlaps final : public PxOverlapCallback
    {
    public:
        CollectOverlaps() : PxOverlapCallback(buffer_, static_cast<PxU32>(sizeof(buffer_) / sizeof(buffer_[0]))) {}

        PxAgain processTouches(const PxOverlapHit* hits, PxU32 count) override
        {
            for (PxU32 index = 0; index < count; ++index)
                if (hits[index].shape)
                    shapes.push_back(hits[index].shape);
            return true;
        }

        void appendRemaining()
        {
            for (PxU32 index = 0; index < nbTouches; ++index)
                if (touches[index].shape)
                    shapes.push_back(touches[index].shape);
        }

        std::vector<PxShape*> shapes;

    private:
        PxOverlapHit buffer_[128]{};
    };

    PxScene* scene = static_cast<PxScene*>(unity_scene);
    const PxVec3 start = to_px(*point0);
    const PxVec3 end = to_px(*point1);
    const PxVec3 delta = end - start;
    const float distance = delta.magnitude();
    const PxVec3 midpoint = (start + end) * 0.5f;
    CollectOverlaps overlaps;
    const PxQueryFilterData filter(PxQueryFlag::eSTATIC | PxQueryFlag::eDYNAMIC | PxQueryFlag::eNO_BLOCK);

    scene->lockRead();
    if (distance <= 0.0001f)
    {
        const PxSphereGeometry sphere(radius);
        scene->overlap(sphere, PxTransform(midpoint), overlaps, filter);
    }
    else
    {
        const PxVec3 direction = delta / distance;
        PxQuat rotation;
        if (direction.x < -0.9999f)
            rotation = PxQuat(PxPi, PxVec3(0.f, 1.f, 0.f));
        else
        {
            const PxVec3 cross(0.f, -direction.z, direction.y);
            rotation = PxQuat(cross.x, cross.y, cross.z, 1.f + direction.x).getNormalized();
        }
        const PxCapsuleGeometry capsule(radius, distance * 0.5f);
        scene->overlap(capsule, PxTransform(midpoint, rotation), overlaps, filter);
    }
    scene->unlockRead();
    overlaps.appendRemaining();

    if (overlaps.shapes.size() > std::numeric_limits<uint32_t>::max())
        return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX proximity query returned too many shapes");
    *written_or_required = static_cast<uint32_t>(overlaps.shapes.size());
    if (overlaps.shapes.size() > capacity)
        return fail(RPTPX_BUFFER_TOO_SMALL, "Unity PhysX proximity shape output buffer is too small");
    for (uint32_t index = 0; index < *written_or_required; ++index)
        shapes[index] = overlaps.shapes[index];
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_unity_extract_mesh(void* shape_pointer, RptPxUnityMeshInfo* info, RptPxVec3* vertices,
    uint32_t vertex_capacity, uint32_t* indices, uint32_t index_capacity)
{
    if (!shape_pointer || !valid_desc(info) || (!vertices && vertex_capacity != 0) || (!indices && index_capacity != 0))
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid Unity PhysX mesh extraction arguments");

    PxShape* shape = static_cast<PxShape*>(shape_pointer);
    const PxGeometryType::Enum geometry_type = shape->getGeometryType();
    const PxVec3* source_vertices = nullptr;
    uint32_t vertex_count = 0;
    uint32_t index_count = 0;
    const void* source_indices = nullptr;
    bool indices_16_bit = false;
    const PxConvexMesh* convex_mesh = nullptr;

    info->geometry_kind = 0;
    info->vertex_count = 0;
    info->index_count = 0;
    info->mesh_identity = 0;
    info->mesh_scale = { 1.f, 1.f, 1.f };
    info->mesh_scale_rotation = { 0.f, 0.f, 0.f, 1.f };
    info->mesh_flags = 0;
    info->index_format = 0;

    if (geometry_type == PxGeometryType::eTRIANGLEMESH)
    {
        PxTriangleMeshGeometry geometry;
        if (!shape->getTriangleMeshGeometry(geometry) || !geometry.triangleMesh)
            return fail(RPTPX_INVALID_HANDLE, "Unity PhysX triangle-mesh geometry is unavailable");
        const PxTriangleMesh* mesh = geometry.triangleMesh;
        vertex_count = mesh->getNbVertices();
        index_count = mesh->getNbTriangles() * 3u;
        source_vertices = mesh->getVertices();
        source_indices = mesh->getTriangles();
        indices_16_bit = mesh->getTriangleMeshFlags().isSet(PxTriangleMeshFlag::e16_BIT_INDICES);
        info->geometry_kind = RPTPX_GEOMETRY_TRIANGLE_MESH;
        info->mesh_identity = reinterpret_cast<uint64_t>(mesh);
        info->mesh_scale = from_px(geometry.scale.scale);
        info->mesh_scale_rotation = from_px(geometry.scale.rotation);
        info->mesh_flags = static_cast<uint32_t>(geometry.meshFlags);
        info->index_format = indices_16_bit ? 16u : 32u;
    }
    else if (geometry_type == PxGeometryType::eCONVEXMESH)
    {
        PxConvexMeshGeometry geometry;
        if (!shape->getConvexMeshGeometry(geometry) || !geometry.convexMesh)
            return fail(RPTPX_INVALID_HANDLE, "Unity PhysX convex-mesh geometry is unavailable");
        convex_mesh = geometry.convexMesh;
        vertex_count = convex_mesh->getNbVertices();
        source_vertices = convex_mesh->getVertices();
        const PxU32 polygon_count = convex_mesh->getNbPolygons();
        uint64_t required_indices = 0;
        for (PxU32 polygon_index = 0; polygon_index < polygon_count; ++polygon_index)
        {
            PxHullPolygon polygon;
            if (!convex_mesh->getPolygonData(polygon_index, polygon) || polygon.mNbVerts < 3)
                return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX convex hull contains an invalid polygon");
            required_indices += static_cast<uint64_t>(polygon.mNbVerts - 2u) * 3u;
        }
        if (required_indices > std::numeric_limits<uint32_t>::max())
            return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX convex hull contains too many indices");
        index_count = static_cast<uint32_t>(required_indices);
        info->geometry_kind = RPTPX_GEOMETRY_CONVEX_MESH;
        info->mesh_identity = reinterpret_cast<uint64_t>(convex_mesh);
        info->mesh_scale = from_px(geometry.scale.scale);
        info->mesh_scale_rotation = from_px(geometry.scale.rotation);
		info->index_format = 8u;
    }
    else
    {
        return fail(RPTPX_INVALID_ARGUMENT, "The Unity PhysX shape is not a triangle or convex mesh");
    }

    if (!source_vertices || vertex_count < 3 || index_count < 3)
        return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX mesh topology is empty");
    info->vertex_count = vertex_count;
    info->index_count = index_count;
    if (vertex_capacity < vertex_count || index_capacity < index_count)
        return fail(RPTPX_BUFFER_TOO_SMALL, "Unity PhysX mesh output buffers are too small");

    for (uint32_t index = 0; index < vertex_count; ++index)
        vertices[index] = from_px(source_vertices[index]);

    if (convex_mesh)
    {
        const PxU8* hull_indices = convex_mesh->getIndexBuffer();
        uint32_t output_index = 0;
        for (PxU32 polygon_index = 0; polygon_index < convex_mesh->getNbPolygons(); ++polygon_index)
        {
            PxHullPolygon polygon;
            if (!convex_mesh->getPolygonData(polygon_index, polygon))
                return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX convex hull polygon disappeared during extraction");
            const PxU8 first = hull_indices[polygon.mIndexBase];
            for (PxU32 triangle = 1; triangle + 1 < polygon.mNbVerts; ++triangle)
            {
                const PxU8 second = hull_indices[polygon.mIndexBase + triangle];
                const PxU8 third = hull_indices[polygon.mIndexBase + triangle + 1];
                if (first >= vertex_count || second >= vertex_count || third >= vertex_count)
                    return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX convex hull index is out of range");
                indices[output_index++] = first;
                indices[output_index++] = second;
                indices[output_index++] = third;
            }
        }
    }
    else if (indices_16_bit)
    {
        const PxU16* source = static_cast<const PxU16*>(source_indices);
        for (uint32_t index = 0; index < index_count; ++index)
            indices[index] = source[index];
    }
    else
    {
        const PxU32* source = static_cast<const PxU32*>(source_indices);
        for (uint32_t index = 0; index < index_count; ++index)
            indices[index] = source[index];
    }

    for (uint32_t index = 0; index < index_count; ++index)
        if (indices[index] >= vertex_count)
            return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX triangle index is out of range");
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_unity_extract_heightfield(void* shape_pointer, RptPxUnityHeightFieldInfo* info,
    uint8_t* samples, uint32_t sample_capacity, RptPxMaterialDesc* materials, uint32_t material_capacity)
{
    if (!shape_pointer || !valid_desc(info) || (!samples && sample_capacity != 0) || (!materials && material_capacity != 0))
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid Unity PhysX heightfield extraction arguments");

    PxShape* shape = static_cast<PxShape*>(shape_pointer);
    if (shape->getGeometryType() != PxGeometryType::eHEIGHTFIELD)
        return fail(RPTPX_INVALID_ARGUMENT, "The Unity PhysX shape is not a heightfield");
    PxHeightFieldGeometry geometry;
    if (!shape->getHeightFieldGeometry(geometry) || !geometry.heightField)
        return fail(RPTPX_INVALID_HANDLE, "Unity PhysX heightfield geometry is unavailable");
    PxRigidActor* actor = shape->getActor();
    if (!actor)
        return fail(RPTPX_INVALID_HANDLE, "Unity PhysX heightfield shape has no actor");

    PxHeightField* heightfield = geometry.heightField;
    const uint32_t rows = heightfield->getNbRows();
    const uint32_t columns = heightfield->getNbColumns();
    const uint32_t sample_stride = heightfield->getSampleStride();
    const uint64_t required_samples = static_cast<uint64_t>(rows) * columns * sample_stride;
    const uint32_t material_count = shape->getNbMaterials();
    if (rows < 2 || columns < 2 || sample_stride < sizeof(PxHeightFieldSample) ||
        required_samples > std::numeric_limits<uint32_t>::max() || material_count == 0)
        return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX heightfield metadata is invalid");

    info->geometry_kind = RPTPX_GEOMETRY_HEIGHTFIELD;
    info->rows = rows;
    info->columns = columns;
    info->sample_stride = sample_stride;
    info->sample_bytes = static_cast<uint32_t>(required_samples);
    info->material_count = material_count;
    info->geometry_flags = static_cast<uint32_t>(geometry.heightFieldFlags);
    info->heightfield_identity = reinterpret_cast<uint64_t>(heightfield);
    info->world_pose = from_px(actor->getGlobalPose() * shape->getLocalPose());
    info->height_scale = geometry.heightScale;
    info->row_scale = geometry.rowScale;
    info->column_scale = geometry.columnScale;
    info->convex_edge_threshold = heightfield->getConvexEdgeThreshold();
    info->heightfield_flags = static_cast<uint32_t>(heightfield->getFlags());
    info->timestamp = heightfield->getTimestamp();

    if (sample_capacity < info->sample_bytes || material_capacity < material_count)
        return fail(RPTPX_BUFFER_TOO_SMALL, "Unity PhysX heightfield output buffers are too small");
    if (!samples || !materials)
        return fail(RPTPX_INVALID_ARGUMENT, "Unity PhysX heightfield output buffers are required");
    if (heightfield->saveCells(samples, sample_capacity) != info->sample_bytes)
        return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX heightfield sample copy was incomplete");

    std::vector<PxMaterial*> source_materials(material_count);
    if (shape->getMaterials(source_materials.data(), material_count) != material_count)
        return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX heightfield material copy was incomplete");
    for (uint32_t index = 0; index < material_count; ++index)
    {
        PxMaterial* material = source_materials[index];
        if (!material)
            return fail(RPTPX_INTERNAL_ERROR, "Unity PhysX heightfield contains a null material");
        materials[index] = {};
        materials[index].struct_size = sizeof(RptPxMaterialDesc);
        materials[index].static_friction = material->getStaticFriction();
        materials[index].dynamic_friction = material->getDynamicFriction();
        materials[index].restitution = material->getRestitution();
        materials[index].friction_combine = static_cast<uint32_t>(material->getFrictionCombineMode());
        materials[index].restitution_combine = static_cast<uint32_t>(material->getRestitutionCombineMode());
    }
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_scene(const RptPxSceneDesc* desc, void** output)
{
    if (!valid_desc(desc) || !output || !finite(desc->gravity) || desc->dispatcher_threads > 4 ||
        desc->solver_type > PxSolverType::eTGS || desc->friction_type >= PxFrictionType::eFRICTION_COUNT ||
        (desc->scene_flags & ~source_scene_flag_mask()) != 0)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid scene descriptor");
    *output = nullptr;
    auto ctx = std::make_unique<SceneContext>();
    ctx->foundation = PxCreateFoundation(PX_PHYSICS_VERSION, ctx->allocator, ctx->errors);
    if (!ctx->foundation) return fail(RPTPX_PHYSX_ERROR, "PxCreateFoundation failed");
    PxTolerancesScale scale;
    ctx->physics = PxCreatePhysics(PX_PHYSICS_VERSION, *ctx->foundation, scale, false, nullptr);
    if (!ctx->physics) return fail(RPTPX_PHYSX_ERROR, "PxCreatePhysics failed");
    if (!PxInitExtensions(*ctx->physics, nullptr)) return fail(RPTPX_PHYSX_ERROR, "PxInitExtensions failed");
    PxCookingParams cooking_params(scale);
    ctx->cooking = PxCreateCooking(PX_PHYSICS_VERSION, *ctx->foundation, cooking_params);
    if (!ctx->cooking) return fail(RPTPX_PHYSX_ERROR, "PxCreateCooking failed");
    if (desc->dispatcher_threads == 0)
        ctx->dispatcher = &ctx->immediate_dispatcher;
    else
    {
        ctx->dispatcher = PxDefaultCpuDispatcherCreate(desc->dispatcher_threads);
        ctx->owns_dispatcher = true;
    }
    if (!ctx->dispatcher) return fail(RPTPX_PHYSX_ERROR, "CPU dispatcher creation failed");
    PxSceneDesc scene_desc(scale);
    scene_desc.gravity = to_px(desc->gravity);
    scene_desc.cpuDispatcher = ctx->dispatcher;
    scene_desc.filterShader = desc->graphical_casings ? casing_filter_shader : filter_shader;
    scene_desc.filterCallback = &ctx->filter_callback;
    scene_desc.solverType = static_cast<PxSolverType::Enum>(desc->solver_type);
    scene_desc.frictionType = static_cast<PxFrictionType::Enum>(desc->friction_type);
    copy_source_scene_flags(scene_desc.flags, desc->scene_flags);
    scene_desc.bounceThresholdVelocity = finite(desc->bounce_threshold_velocity) && desc->bounce_threshold_velocity > 0.f
        ? desc->bounce_threshold_velocity : 2.f;
    if (desc->graphical_casings)
    {
        // PxFlags::set replaces the mask. Restore all requested flags for this
        // graphical scene and require CCD for tiny casings. The existing
        // headless scene setup remains untouched.
        scene_desc.flags = PxSceneFlags((static_cast<PxU32>(scene_desc.flags) & ~source_scene_flag_mask()) |
            desc->scene_flags | PxSceneFlag::eENABLE_CCD | PxSceneFlag::eENABLE_ACTIVE_ACTORS);
    }
    ctx->scene = ctx->physics->createScene(scene_desc);
    if (!ctx->scene) return fail(RPTPX_PHYSX_ERROR, "PxPhysics::createScene failed");
    if (desc->graphical_casings) ctx->casings = std::make_unique<CasingWorld>(*ctx->physics, *ctx->scene);
    *output = ctx.release();
    g_last_error.clear();
    return RPTPX_OK;
}

void RPTPX_CALL rptpx_destroy_scene(void* scene) { delete context(scene); }

int32_t RPTPX_CALL rptpx_create_material(void* scene, const RptPxMaterialDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid material arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxMaterial* material = ctx->physics->createMaterial(std::max(0.f, desc->static_friction), std::max(0.f, desc->dynamic_friction), std::max(0.f, desc->restitution));
    if (!material) return fail(RPTPX_PHYSX_ERROR, "Material creation failed");
    material->setFrictionCombineMode(combine_mode(desc->friction_combine));
    material->setRestitutionCombineMode(combine_mode(desc->restitution_combine));
    *output = ctx->materials.add(material);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_cook_mesh(void* scene, const RptPxMeshDesc* desc, uint8_t* output, uint32_t capacity, uint32_t* required)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !required)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid mesh cooking arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    if (desc)
    {
        ctx->pending_cooked_mesh.clear();
        if (!valid_desc(desc) || !desc->vertices || desc->vertex_count < 3 || (!desc->convex && (!desc->indices || desc->index_count < 3 || desc->index_count % 3 != 0)))
            return fail(RPTPX_INVALID_ARGUMENT, "Invalid mesh cooking descriptor");
        PxDefaultMemoryOutputStream cooked;
        if (!cook_mesh(*ctx, *desc, cooked)) return fail(RPTPX_PHYSX_ERROR, g_last_error.empty() ? "PhysX mesh cooking failed" : g_last_error);
        ctx->pending_cooked_mesh.assign(cooked.getData(), cooked.getData() + cooked.getSize());
    }
    else if (ctx->pending_cooked_mesh.empty())
        return fail(RPTPX_INVALID_ARGUMENT, "No pending cooked mesh result");
    *required = static_cast<uint32_t>(ctx->pending_cooked_mesh.size());
    if (!output || capacity < *required) return RPTPX_BUFFER_TOO_SMALL;
    std::copy(ctx->pending_cooked_mesh.begin(), ctx->pending_cooked_mesh.end(), output);
    ctx->pending_cooked_mesh.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_mesh_from_cooked(void* scene, uint32_t convex, const uint8_t* data, uint32_t length, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !data || !length || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid cooked mesh arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    MeshResource* mesh = create_mesh_resource(*ctx, convex != 0, data, length);
    if (!mesh) return fail(RPTPX_PHYSX_ERROR, "Cooked mesh creation failed");
    *output = ctx->meshes.add(mesh);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_mesh(void* scene, const RptPxMeshDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid mesh arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxDefaultMemoryOutputStream cooked;
    if (!cook_mesh(*ctx, *desc, cooked)) return fail(RPTPX_PHYSX_ERROR, g_last_error.empty() ? "PhysX mesh cooking failed" : g_last_error);
    MeshResource* mesh = create_mesh_resource(*ctx, desc->convex != 0, cooked.getData(), cooked.getSize());
    if (!mesh) return fail(RPTPX_PHYSX_ERROR, "Mesh creation failed after cooking");
    *output = ctx->meshes.add(mesh);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_heightfield(void* scene, const RptPxHeightFieldDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output || !desc->samples || desc->rows < 2 || desc->columns < 2 ||
        desc->sample_stride < sizeof(PxHeightFieldSample) || !std::isfinite(desc->convex_edge_threshold) ||
        desc->convex_edge_threshold < 0.f)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid heightfield arguments");
    const uint64_t required = static_cast<uint64_t>(desc->rows) * desc->columns * desc->sample_stride;
    if (required > desc->sample_bytes)
        return fail(RPTPX_INVALID_ARGUMENT, "Heightfield sample buffer is too small");

    std::lock_guard<std::mutex> lock(ctx->gate);
    PxHeightFieldDesc native_desc;
    native_desc.nbRows = desc->rows;
    native_desc.nbColumns = desc->columns;
    native_desc.format = PxHeightFieldFormat::eS16_TM;
    native_desc.samples.data = desc->samples;
    native_desc.samples.stride = desc->sample_stride;
    native_desc.convexEdgeThreshold = desc->convex_edge_threshold;
    native_desc.flags = PxHeightFieldFlags(static_cast<PxU16>(desc->flags));
    if (!native_desc.isValid())
        return fail(RPTPX_INVALID_ARGUMENT, "Heightfield descriptor is invalid for PhysX 4.1.2");
    PxHeightField* heightfield = ctx->cooking->createHeightField(native_desc, ctx->physics->getPhysicsInsertionCallback());
    if (!heightfield)
        return fail(RPTPX_PHYSX_ERROR, g_last_error.empty() ? "Heightfield creation failed" : g_last_error);
    ctx->heightfield_owners.push_back(heightfield);
    *output = ctx->heightfields.add(heightfield);
    g_last_error.clear();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_actor(void* scene, const RptPxBodyDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output || !finite(desc->pose.position) || !finite(desc->pose.rotation))
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid actor arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor = nullptr;
    if (desc->kind == RPTPX_ACTOR_STATIC)
        actor = ctx->physics->createRigidStatic(to_px(desc->pose));
    else if (desc->kind == RPTPX_ACTOR_DYNAMIC || desc->kind == RPTPX_ACTOR_KINEMATIC)
    {
        PxRigidDynamic* dynamic = ctx->physics->createRigidDynamic(to_px(desc->pose));
        if (dynamic)
        {
            dynamic->setMass(std::max(0.0001f, desc->mass));
            dynamic->setLinearDamping(std::max(0.f, desc->linear_damping));
            dynamic->setAngularDamping(std::max(0.f, desc->angular_damping));
            dynamic->setSleepThreshold(std::max(0.f, desc->sleep_threshold));
            dynamic->setMaxAngularVelocity(std::max(0.f, desc->max_angular_velocity));
            dynamic->setMaxDepenetrationVelocity(std::max(0.f, desc->max_depenetration_velocity));
            dynamic->setSolverIterationCounts(std::max(1u, desc->solver_position_iterations), std::max(1u, desc->solver_velocity_iterations));
            dynamic->setLinearVelocity(to_px(desc->linear_velocity), false);
            dynamic->setAngularVelocity(to_px(desc->angular_velocity), false);
            dynamic->setRigidBodyFlag(PxRigidBodyFlag::eKINEMATIC, desc->kind == RPTPX_ACTOR_KINEMATIC);
            dynamic->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_CCD, (desc->flags & 1u) != 0 && desc->kind == RPTPX_ACTOR_DYNAMIC);
			if ((desc->flags & 1u) != 0) ctx->ccd_actors.insert(dynamic);
            dynamic->setActorFlag(PxActorFlag::eDISABLE_GRAVITY, (desc->flags & 2u) != 0);
            PxRigidDynamicLockFlags locks;
            if (desc->flags & 4u) locks |= PxRigidDynamicLockFlag::eLOCK_LINEAR_X;
            if (desc->flags & 8u) locks |= PxRigidDynamicLockFlag::eLOCK_LINEAR_Y;
            if (desc->flags & 16u) locks |= PxRigidDynamicLockFlag::eLOCK_LINEAR_Z;
            if (desc->flags & 32u) locks |= PxRigidDynamicLockFlag::eLOCK_ANGULAR_X;
            if (desc->flags & 64u) locks |= PxRigidDynamicLockFlag::eLOCK_ANGULAR_Y;
            if (desc->flags & 128u) locks |= PxRigidDynamicLockFlag::eLOCK_ANGULAR_Z;
            dynamic->setRigidDynamicLockFlags(locks);
            dynamic->setCMassLocalPose(PxTransform(to_px(desc->center_of_mass), to_px(desc->inertia_rotation)));
            const PxVec3 inertia = to_px(desc->inertia_tensor);
            if (inertia.x > 0.f && inertia.y > 0.f && inertia.z > 0.f) dynamic->setMassSpaceInertiaTensor(inertia);
        }
        actor = dynamic;
    }
    if (!actor) return fail(RPTPX_PHYSX_ERROR, "Actor creation failed");
    ctx->scene->addActor(*actor);
    *output = ctx->actors.add(actor);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_attach_shape(void* scene, uint64_t actor_handle, const RptPxShapeDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid shape arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor = ctx->actors.get(actor_handle);
    PxMaterial* material = ctx->materials.get(desc->material_handle);
    return attach_shape_locked(*ctx, actor, *desc, &material, 1, output);
}

int32_t RPTPX_CALL rptpx_attach_shape_materials(void* scene, uint64_t actor_handle, const RptPxShapeDesc* desc,
    const uint64_t* material_handles, uint32_t material_count, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output || !material_handles || material_count == 0 || material_count > std::numeric_limits<PxU16>::max())
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid multi-material shape arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor = ctx->actors.get(actor_handle);
    std::vector<PxMaterial*> materials(material_count);
    for (uint32_t index = 0; index < material_count; ++index)
    {
        materials[index] = ctx->materials.get(material_handles[index]);
        if (!materials[index]) return fail(RPTPX_INVALID_HANDLE, "A shape material handle is stale");
    }
    return attach_shape_locked(*ctx, actor, *desc, materials.data(), static_cast<PxU16>(material_count), output);
}

int32_t RPTPX_CALL rptpx_update_mass_and_inertia(void* scene, uint64_t actor_handle, float mass, RptPxVec3* center_of_mass)
{
	SceneContext* ctx = context(scene);
	if (!ctx || !center_of_mass || !std::isfinite(mass) || mass <= 0.f)
		return fail(RPTPX_INVALID_ARGUMENT, "Invalid mass-property update arguments");
	std::lock_guard<std::mutex> lock(ctx->gate);
	PxRigidActor* actor = ctx->actors.get(actor_handle);
	PxRigidDynamic* dynamic = actor ? actor->is<PxRigidDynamic>() : nullptr;
	if (!dynamic) return fail(RPTPX_INVALID_HANDLE, "Mass-property actor handle is not dynamic");
	if (!PxRigidBodyExt::setMassAndUpdateInertia(*dynamic, mass))
		return fail(RPTPX_PHYSX_ERROR, "PhysX could not compute mass properties from the actor shapes");
	const PxVec3 center = dynamic->getCMassLocalPose().p;
	*center_of_mass = { center.x, center.y, center.z };
	g_last_error.clear();
	return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_destroy_shape(void* scene, uint64_t handle)
{
    SceneContext* ctx = context(scene);
    if (!ctx) return fail(RPTPX_INVALID_ARGUMENT, "Scene is required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxShape* shape = ctx->shapes.remove(handle);
    if (!shape) return fail(RPTPX_INVALID_HANDLE, "Shape handle is stale");
    if (shape->getActor()) shape->getActor()->detachShape(*shape);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_destroy_mesh(void* scene, uint64_t handle)
{
    SceneContext* ctx = context(scene);
    if (!ctx) return fail(RPTPX_INVALID_ARGUMENT, "Scene is required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    MeshResource* mesh = ctx->meshes.remove(handle);
    if (!mesh) return fail(RPTPX_INVALID_HANDLE, "Mesh handle is stale");
    auto owner = std::find_if(ctx->mesh_owners.begin(), ctx->mesh_owners.end(),
        [&](const std::unique_ptr<MeshResource>& candidate) { return candidate.get() == mesh; });
    if (owner == ctx->mesh_owners.end()) return fail(RPTPX_INVALID_HANDLE, "Mesh owner is missing");
    mesh->release();
    ctx->mesh_owners.erase(owner);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_destroy_actor(void* scene, uint64_t handle)
{
    SceneContext* ctx = context(scene);
    if (!ctx) return fail(RPTPX_INVALID_ARGUMENT, "Scene is required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor = ctx->actors.remove(handle);
    if (!actor) return fail(RPTPX_INVALID_HANDLE, "Actor handle is stale");
	if (auto* dynamic = actor->is<PxRigidDynamic>()) ctx->ccd_actors.erase(dynamic);
    const PxU32 count = actor->getNbShapes();
    std::vector<PxShape*> attached(count);
    if (count) actor->getShapes(attached.data(), count);
    for (PxShape* shape : attached)
    {
        ctx->shapes.remove_value(shape);
    }
    actor->release();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_destroy_joint(void* scene, uint64_t handle)
{
    SceneContext* ctx = context(scene);
    if (!ctx) return fail(RPTPX_INVALID_ARGUMENT, "Scene is required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxJoint* joint = ctx->joints.remove(handle);
    if (!joint) return fail(RPTPX_INVALID_HANDLE, "Joint handle is stale");
    joint->release();
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_get_joint_broken(void* scene, uint64_t handle, uint32_t* broken)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !broken) return fail(RPTPX_INVALID_ARGUMENT, "Scene and broken output are required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxJoint* joint = ctx->joints.get(handle);
    if (!joint) return fail(RPTPX_INVALID_HANDLE, "Joint handle is stale");
    *broken = (joint->getConstraintFlags() & PxConstraintFlag::eBROKEN) ? 1u : 0u;
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_set_actor_pose(void* scene, uint64_t handle, const RptPxPose* pose, uint32_t kinematic_target)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !pose) return fail(RPTPX_INVALID_ARGUMENT, "Invalid pose arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor = ctx->actors.get(handle);
    if (!actor) return fail(RPTPX_INVALID_HANDLE, "Actor handle is stale");
    PxRigidDynamic* dynamic = actor->is<PxRigidDynamic>();
    if (kinematic_target && dynamic && (dynamic->getRigidBodyFlags() & PxRigidBodyFlag::eKINEMATIC)) dynamic->setKinematicTarget(to_px(*pose));
    else actor->setGlobalPose(to_px(*pose), true);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_set_actor_state(void* scene, uint64_t handle, const RptPxBodyState* state, uint32_t kinematic, uint32_t wake)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(state)) return fail(RPTPX_INVALID_ARGUMENT, "Invalid actor state arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidDynamic* actor = ctx->actors.get(handle) ? ctx->actors.get(handle)->is<PxRigidDynamic>() : nullptr;
    if (!actor) return fail(RPTPX_INVALID_HANDLE, "Dynamic actor handle is stale");
    const bool was_kinematic = actor->getRigidBodyFlags().isSet(PxRigidBodyFlag::eKINEMATIC);
    const bool will_be_kinematic = kinematic != 0;
    if (was_kinematic != will_be_kinematic && !was_kinematic)
    {
        actor->clearForce(PxForceMode::eFORCE);
        actor->clearForce(PxForceMode::eIMPULSE);
        actor->clearTorque(PxForceMode::eFORCE);
        actor->clearTorque(PxForceMode::eIMPULSE);
    }
	if (will_be_kinematic) actor->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_CCD, false);
    actor->setRigidBodyFlag(PxRigidBodyFlag::eKINEMATIC, will_be_kinematic);
	if (!will_be_kinematic) actor->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_CCD, ctx->ccd_actors.count(actor) != 0);
    if (was_kinematic != will_be_kinematic && !will_be_kinematic)
    {
        // PhysX resets its velocity-modification state on this transition. Clear
        // both public accumulators as well so an interrupted prediction step can
        // never leak a force or impulse into the next dynamic ownership epoch.
        actor->clearForce(PxForceMode::eFORCE);
        actor->clearForce(PxForceMode::eIMPULSE);
        actor->clearTorque(PxForceMode::eFORCE);
        actor->clearTorque(PxForceMode::eIMPULSE);
    }
    actor->setGlobalPose(to_px(state->pose), false);
    if (!will_be_kinematic)
    {
        actor->setLinearVelocity(to_px(state->linear_velocity), false);
        actor->setAngularVelocity(to_px(state->angular_velocity), false);
        if (wake) actor->wakeUp(); else actor->putToSleep();
    }
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_set_shape_enabled(void* scene, uint64_t handle, uint32_t enabled)
{
    SceneContext* ctx = context(scene);
    if (!ctx) return fail(RPTPX_INVALID_ARGUMENT, "Scene is required");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxShape* shape = ctx->shapes.get(handle);
    if (!shape) return fail(RPTPX_INVALID_HANDLE, "Shape handle is stale");
    shape->setFlag(PxShapeFlag::eSIMULATION_SHAPE, enabled != 0);
    if (ctx->casings) shape->setFlag(PxShapeFlag::eSCENE_QUERY_SHAPE, enabled != 0);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_ignore_shape_pair(void* scene, uint64_t shape0_handle, uint64_t shape1_handle)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !shape0_handle || !shape1_handle || shape0_handle == shape1_handle)
        return fail(RPTPX_INVALID_ARGUMENT, "Invalid ignored shape-pair arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxShape* shape0 = ctx->shapes.get(shape0_handle);
    PxShape* shape1 = ctx->shapes.get(shape1_handle);
    if (!shape0 || !shape1) return fail(RPTPX_INVALID_HANDLE, "Ignored shape-pair handle is stale");
    const uint32_t id0 = shape0->getSimulationFilterData().word3;
    const uint32_t id1 = shape1->getSimulationFilterData().word3;
    if (!id0 || !id1 || id0 == id1) return fail(RPTPX_INVALID_ARGUMENT, "Ignored shape-pair ids are invalid");
    ctx->filter_callback.ignored_pairs.insert(filter_pair_key(id0, id1));
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_d6_joint(void* scene, const RptPxD6JointDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid D6 joint arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor0 = actor_for_joint(*ctx, desc->actor0);
    PxRigidActor* actor1 = actor_for_joint(*ctx, desc->actor1);
    if ((!actor0 && desc->actor0) || (!actor1 && desc->actor1) || actor0 == actor1) return fail(RPTPX_INVALID_HANDLE, "D6 joint actor handle is stale");
    PxD6Joint* joint = PxD6JointCreate(*ctx->physics, actor0, to_px(desc->local_pose0), actor1, to_px(desc->local_pose1));
    if (!joint) return fail(RPTPX_PHYSX_ERROR, "D6 joint creation failed");
    joint->setMotion(PxD6Axis::eX, PxD6Motion::eLOCKED);
    joint->setMotion(PxD6Axis::eY, PxD6Motion::eLOCKED);
    joint->setMotion(PxD6Axis::eZ, PxD6Motion::eLOCKED);
	constexpr float angular_epsilon = 1e-4f;
	const float twist_lower = std::min(desc->twist_lower, desc->twist_upper);
	const float twist_upper = std::max(desc->twist_lower, desc->twist_upper);
	const bool twist_limited = twist_upper - twist_lower > angular_epsilon;
	joint->setMotion(PxD6Axis::eTWIST, twist_limited ? PxD6Motion::eLIMITED : PxD6Motion::eLOCKED);
	if (twist_limited)
	{
		PxJointAngularLimitPair twist(twist_lower, twist_upper, std::max(0.f, desc->twist_contact_distance));
		twist.stiffness = std::max(0.f, desc->twist_stiffness);
		twist.damping = std::max(0.f, desc->twist_damping);
		twist.restitution = std::max(0.f, std::min(1.f, desc->twist_restitution));
		joint->setTwistLimit(twist);
	}

	const bool swing_y_limited = desc->swing_y > angular_epsilon;
	const bool swing_z_limited = desc->swing_z > angular_epsilon;
	joint->setMotion(PxD6Axis::eSWING1, swing_y_limited ? PxD6Motion::eLIMITED : PxD6Motion::eLOCKED);
	joint->setMotion(PxD6Axis::eSWING2, swing_z_limited ? PxD6Motion::eLIMITED : PxD6Motion::eLOCKED);
	if (swing_y_limited || swing_z_limited)
	{
		// A locked swing axis is constrained independently. Give the cone a round
		// cross-section for its remaining limited axis instead of an extremely thin
		// ellipse, which PhysX documents as a source of constraint jitter.
		const float cone_y = swing_y_limited ? desc->swing_y : desc->swing_z;
		const float cone_z = swing_z_limited ? desc->swing_z : desc->swing_y;
		PxJointLimitCone swing(std::max(angular_epsilon, cone_y), std::max(angular_epsilon, cone_z), std::max(0.f, desc->swing_contact_distance));
		swing.stiffness = std::max(0.f, desc->swing_stiffness);
		swing.damping = std::max(0.f, desc->swing_damping);
		swing.restitution = std::max(0.f, std::min(1.f, desc->swing_restitution));
		joint->setSwingLimit(swing);
	}
    joint->setProjectionLinearTolerance(std::max(0.f, desc->projection_linear_tolerance));
    joint->setProjectionAngularTolerance(std::max(0.f, desc->projection_angular_tolerance));
    configure_joint(*joint, desc->break_force, desc->break_torque, desc->projection != 0,
		desc->mass_scale, desc->connected_mass_scale, desc->enable_preprocessing != 0);
    *output = ctx->joints.add(joint);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_fixed_joint(void* scene, const RptPxFixedJointDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid fixed joint arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor0 = actor_for_joint(*ctx, desc->actor0);
    PxRigidActor* actor1 = actor_for_joint(*ctx, desc->actor1);
    if ((!actor0 && desc->actor0) || (!actor1 && desc->actor1) || actor0 == actor1) return fail(RPTPX_INVALID_HANDLE, "Fixed joint actor handle is stale");
    PxFixedJoint* joint = PxFixedJointCreate(*ctx->physics, actor0, to_px(desc->local_pose0), actor1, to_px(desc->local_pose1));
    if (!joint) return fail(RPTPX_PHYSX_ERROR, "Fixed joint creation failed");
    configure_joint(*joint, desc->break_force, desc->break_torque, desc->projection != 0,
		desc->mass_scale, desc->connected_mass_scale, desc->enable_preprocessing != 0);
    *output = ctx->joints.add(joint);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_create_distance_joint(void* scene, const RptPxDistanceJointDesc* desc, uint64_t* output)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !valid_desc(desc) || !output) return fail(RPTPX_INVALID_ARGUMENT, "Invalid distance joint arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    PxRigidActor* actor0 = actor_for_joint(*ctx, desc->actor0);
    PxRigidActor* actor1 = actor_for_joint(*ctx, desc->actor1);
    if ((!actor0 && desc->actor0) || (!actor1 && desc->actor1) || actor0 == actor1) return fail(RPTPX_INVALID_HANDLE, "Distance joint actor handle is stale");
    PxDistanceJoint* joint = PxDistanceJointCreate(*ctx->physics, actor0, to_px(desc->local_pose0), actor1, to_px(desc->local_pose1));
    if (!joint) return fail(RPTPX_PHYSX_ERROR, "Distance joint creation failed");
    joint->setMinDistance(std::max(0.f, desc->minimum_distance));
    joint->setMaxDistance(std::max(desc->minimum_distance, desc->maximum_distance));
    joint->setStiffness(std::max(0.f, desc->stiffness));
    joint->setDamping(std::max(0.f, desc->damping));
    joint->setTolerance(std::max(0.f, desc->tolerance));
    joint->setDistanceJointFlag(PxDistanceJointFlag::eMIN_DISTANCE_ENABLED, true);
    joint->setDistanceJointFlag(PxDistanceJointFlag::eMAX_DISTANCE_ENABLED, true);
    joint->setDistanceJointFlag(PxDistanceJointFlag::eSPRING_ENABLED, desc->stiffness > 0.f || desc->damping > 0.f);
    configure_joint(*joint, desc->break_force, desc->break_torque, false,
		desc->mass_scale, desc->connected_mass_scale, desc->enable_preprocessing != 0);
    *output = ctx->joints.add(joint);
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_apply_forces(void* scene, const RptPxForceCommand* commands, uint32_t count)
{
    SceneContext* ctx = context(scene);
    if (!ctx || (count && !commands)) return fail(RPTPX_INVALID_ARGUMENT, "Invalid force command arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    for (uint32_t index = 0; index < count; ++index)
    {
        const RptPxForceCommand& command = commands[index];
        if (command.struct_size != sizeof(RptPxForceCommand)) return fail(RPTPX_INVALID_ARGUMENT, "Invalid force command size");
        PxRigidDynamic* actor = ctx->actors.get(command.actor) ? ctx->actors.get(command.actor)->is<PxRigidDynamic>() : nullptr;
        if (!actor) return fail(RPTPX_INVALID_HANDLE, "Force actor handle is stale");
        const PxForceMode::Enum mode = force_mode(command.mode);
        if (command.operation == RPTPX_ADD_FORCE)
            actor->addForce(to_px(command.force), mode, command.wake != 0);
        else if (command.operation == RPTPX_ADD_TORQUE)
            actor->addTorque(to_px(command.force), mode, command.wake != 0);
        else if (command.operation == RPTPX_ADD_FORCE_AT_POSITION)
            PxRigidBodyExt::addForceAtPos(*actor, to_px(command.force), to_px(command.position), mode, command.wake != 0);
        else
            return fail(RPTPX_INVALID_ARGUMENT, "Unknown force operation");
    }
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_step(void* scene, float delta_time, RptPxBodyState* states, uint32_t capacity, uint32_t* written_or_required)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !written_or_required || !finite(delta_time) || delta_time <= 0.f) return fail(RPTPX_INVALID_ARGUMENT, "Invalid step arguments");
    std::lock_guard<std::mutex> lock(ctx->gate);
    if (ctx->simulating) return fail(RPTPX_BUSY, "A simulation step is already in flight");
    uint32_t required = 0;
    ctx->actors.each([&](PxRigidActor* actor) { if (actor->is<PxRigidDynamic>()) ++required; });
    *written_or_required = required;
    if (capacity < required || (required && !states)) return RPTPX_BUFFER_TOO_SMALL;
    ctx->simulating = true;
    ctx->scene->simulate(delta_time);
    const bool fetched = ctx->scene->fetchResults(true);
    ctx->simulating = false;
    if (!fetched) return fail(RPTPX_PHYSX_ERROR, "PxScene::fetchResults failed");
    if (ctx->casings) ctx->casings->capture();
    uint32_t written = 0;
    ctx->actors.each_handle([&](uint64_t handle, PxRigidActor* actor) {
        PxRigidDynamic* dynamic = actor->is<PxRigidDynamic>();
        if (!dynamic) return;
        RptPxBodyState& state = states[written++];
        state.struct_size = sizeof(RptPxBodyState);
        state.sleeping = dynamic->isSleeping() ? 1u : 0u;
        state.actor = handle;
        state.pose = from_px(dynamic->getGlobalPose());
        state.linear_velocity = from_px(dynamic->getLinearVelocity());
        state.angular_velocity = from_px(dynamic->getAngularVelocity());
    });
    *written_or_required = written;
    return RPTPX_OK;
}

int32_t RPTPX_CALL rptpx_casing_commands(void* scene,
    const RptPxCasingDesc* creates, uint32_t create_count, const uint64_t* removes, uint32_t remove_count,
    const RptPxCasingImpact* impacts, uint32_t impact_count)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !ctx->casings || (create_count && !creates) || (remove_count && !removes) || (impact_count && !impacts))
        return fail(RPTPX_INVALID_ARGUMENT, "Casing commands require a graphical casing scene");
    std::lock_guard<std::mutex> lock(ctx->gate);
    for (uint32_t i = 0; i < create_count; ++i) ctx->casings->create(creates[i]);
    ctx->casings->remove_batch(removes, remove_count);
    for (uint32_t i = 0; i < impact_count; ++i) ctx->casings->apply(impacts[i]);
    return RPTPX_OK;
}
int32_t RPTPX_CALL rptpx_casing_read(void* scene,
    RptPxCasingState* states, uint32_t state_capacity, uint32_t* state_count,
    RptPxCasingContact* contacts, uint32_t contact_capacity, uint32_t* contact_count)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !ctx->casings || !state_count || !contact_count) return fail(RPTPX_INVALID_ARGUMENT, "Casing read requires a graphical casing scene");
    std::lock_guard<std::mutex> lock(ctx->gate);
    return ctx->casings->read(states, state_capacity, *state_count, contacts, contact_capacity, *contact_count);
}

int32_t RPTPX_CALL rptpx_casing_audio(void* scene, RptPxVec3 listener, uint32_t enabled)
{
    SceneContext* ctx = context(scene);
    if (!ctx || !ctx->casings || !finite(listener)) return fail(RPTPX_INVALID_ARGUMENT, "Casing audio requires a graphical casing scene");
    std::lock_guard<std::mutex> lock(ctx->gate);
    ctx->casings->audio(to_px(listener), enabled != 0);
    return RPTPX_OK;
}
