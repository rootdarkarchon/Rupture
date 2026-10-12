#pragma once

#include <stdint.h>

#if defined(_WIN32)
#  if defined(RPT_PHYSX_BRIDGE_EXPORTS)
#    define RPTPX_API extern "C" __declspec(dllexport)
#  else
#    define RPTPX_API extern "C" __declspec(dllimport)
#  endif
#  define RPTPX_CALL __cdecl
#else
#  define RPTPX_API extern "C"
#  define RPTPX_CALL
#endif

enum : uint32_t
{
    RPTPX_ABI_VERSION = 10,
    RPTPX_PHYSX_VERSION = 0x04010200,
};

// Independent, versioned codec entry point; no physics initialization or Unity calls.
// Input is a Unity LZMA block: five property bytes followed by the raw LZMA stream.
RPTPX_API int32_t RPTPX_CALL rptpx_lzma_decode_v1(const uint8_t* source, uint32_t source_size,
    uint8_t* destination, uint32_t destination_size);

enum RptPxResult : int32_t
{
    RPTPX_OK = 0,
    RPTPX_INVALID_ARGUMENT = 1,
    RPTPX_INVALID_HANDLE = 2,
    RPTPX_PHYSX_ERROR = 3,
    RPTPX_BUFFER_TOO_SMALL = 4,
    RPTPX_BUSY = 5,
    RPTPX_INTERNAL_ERROR = 6,
};

enum RptPxActorKind : uint32_t
{
    RPTPX_ACTOR_STATIC = 0,
    RPTPX_ACTOR_DYNAMIC = 1,
    RPTPX_ACTOR_KINEMATIC = 2,
};

enum RptPxGeometryKind : uint32_t
{
    RPTPX_GEOMETRY_BOX = 1,
    RPTPX_GEOMETRY_SPHERE = 2,
    RPTPX_GEOMETRY_CAPSULE = 3,
    RPTPX_GEOMETRY_TRIANGLE_MESH = 4,
    RPTPX_GEOMETRY_CONVEX_MESH = 5,
    RPTPX_GEOMETRY_HEIGHTFIELD = 6,
};

enum RptPxForceMode : uint32_t
{
    RPTPX_FORCE = 0,
    RPTPX_IMPULSE = 1,
    RPTPX_VELOCITY_CHANGE = 2,
    RPTPX_ACCELERATION = 3,
};

enum RptPxForceOperation : uint32_t
{
    RPTPX_ADD_FORCE = 1,
    RPTPX_ADD_FORCE_AT_POSITION = 2,
    RPTPX_ADD_TORQUE = 3,
};

struct RptPxVec3 { float x, y, z; };
struct RptPxQuat { float x, y, z, w; };
struct RptPxPose { RptPxVec3 position; RptPxQuat rotation; };

struct RptPxSceneDesc
{
    uint32_t struct_size;
    uint32_t dispatcher_threads;
    RptPxVec3 gravity;
    float bounce_threshold_velocity;
    uint32_t solver_type;
    uint32_t friction_type;
    uint32_t scene_flags;
    uint32_t graphical_casings;
};

// Raid-local identities, never actor pointers or network identities.
struct RptPxCasingDesc
{
    uint64_t id;
    RptPxPose pose, shape_pose;
    RptPxVec3 velocity, angular_velocity;
    float radius, half_height, mass;
    uint32_t layer_bits, collision_mask, box;
};
struct RptPxCasingImpact
{
    uint64_t event_id;
    uint32_t kind; // 0: clipped bullet segment, 1: blast, 2: surface scatter
    RptPxVec3 start, end, direction;
    float radius, strength;
};
struct RptPxCasingState
{
    uint64_t id;
    RptPxPose pose;
    RptPxVec3 velocity;
    uint32_t flags; // 1: sleeping, 2: creation failed
};
struct RptPxCasingContact
{
    uint64_t id;
    RptPxVec3 position, normal;
    float speed;
};
RPTPX_API int32_t RPTPX_CALL rptpx_casing_commands(void* scene,
    const RptPxCasingDesc* creates, uint32_t create_count, const uint64_t* removes, uint32_t remove_count,
    const RptPxCasingImpact* impacts, uint32_t impact_count);
RPTPX_API int32_t RPTPX_CALL rptpx_casing_read(void* scene,
    RptPxCasingState* states, uint32_t state_capacity, uint32_t* state_count,
    RptPxCasingContact* contacts, uint32_t contact_capacity, uint32_t* contact_count);
RPTPX_API int32_t RPTPX_CALL rptpx_casing_audio(void* scene, RptPxVec3 listener, uint32_t enabled);

struct RptPxUnitySceneSettings
{
    uint32_t struct_size;
    uint32_t solver_type;
    uint32_t friction_type;
    uint32_t scene_flags;
};

struct RptPxMaterialDesc
{
    uint32_t struct_size;
    float static_friction;
    float dynamic_friction;
    float restitution;
    uint32_t friction_combine;
    uint32_t restitution_combine;
};

struct RptPxBodyDesc
{
    uint32_t struct_size;
    uint32_t kind;
    RptPxPose pose;
    RptPxVec3 linear_velocity;
    RptPxVec3 angular_velocity;
    float mass;
    float linear_damping;
    float angular_damping;
    float sleep_threshold;
    float max_angular_velocity;
    float max_depenetration_velocity;
    uint32_t solver_position_iterations;
    uint32_t solver_velocity_iterations;
    uint32_t flags;
    RptPxVec3 center_of_mass;
    RptPxVec3 inertia_tensor;
    RptPxQuat inertia_rotation;
};

struct RptPxShapeDesc
{
    uint32_t struct_size;
    uint32_t geometry_kind;
    uint64_t mesh_handle;
    uint64_t material_handle;
    RptPxPose local_pose;
    RptPxVec3 dimensions;
    RptPxVec3 mesh_scale;
    uint32_t geometry_flags;
    uint32_t layer_bits;
    uint32_t collides_with_bits;
    uint32_t owner_group;
    uint32_t shape_id;
    float contact_offset;
    uint32_t enabled;
};

struct RptPxMeshDesc
{
    uint32_t struct_size;
    uint32_t convex;
    const RptPxVec3* vertices;
    uint32_t vertex_count;
    const uint32_t* indices;
    uint32_t index_count;
    uint32_t cooking_flags;
};

struct RptPxUnityMeshInfo
{
    uint32_t struct_size;
    uint32_t geometry_kind;
    uint32_t vertex_count;
    uint32_t index_count;
    uint64_t mesh_identity;
    RptPxVec3 mesh_scale;
    RptPxQuat mesh_scale_rotation;
    uint32_t mesh_flags;
    uint32_t index_format;
};

struct RptPxUnityHeightFieldInfo
{
    uint32_t struct_size;
    uint32_t geometry_kind;
    uint32_t rows;
    uint32_t columns;
    uint32_t sample_stride;
    uint32_t sample_bytes;
    uint32_t material_count;
    uint32_t geometry_flags;
    uint64_t heightfield_identity;
    RptPxPose world_pose;
    float height_scale;
    float row_scale;
    float column_scale;
    float convex_edge_threshold;
    uint32_t heightfield_flags;
    uint32_t timestamp;
};

struct RptPxHeightFieldDesc
{
    uint32_t struct_size;
    uint32_t rows;
    uint32_t columns;
    uint32_t sample_stride;
    const uint8_t* samples;
    uint32_t sample_bytes;
    float convex_edge_threshold;
    uint32_t flags;
};

struct RptPxD6JointDesc
{
    uint32_t struct_size;
    uint64_t actor0;
    uint64_t actor1;
    RptPxPose local_pose0;
    RptPxPose local_pose1;
    RptPxVec3 linear_limit;
    float twist_lower;
    float twist_upper;
    float swing_y;
    float swing_z;
    float twist_stiffness;
    float twist_damping;
    float twist_restitution;
    float twist_contact_distance;
    float swing_stiffness;
    float swing_damping;
    float swing_restitution;
    float swing_contact_distance;
    float break_force;
    float break_torque;
    uint32_t projection;
    float projection_linear_tolerance;
    float projection_angular_tolerance;
    float mass_scale;
    float connected_mass_scale;
    uint32_t enable_preprocessing;
};

struct RptPxFixedJointDesc
{
    uint32_t struct_size;
    uint64_t actor0;
    uint64_t actor1;
    RptPxPose local_pose0;
    RptPxPose local_pose1;
    float break_force;
    float break_torque;
    uint32_t projection;
    float mass_scale;
    float connected_mass_scale;
    uint32_t enable_preprocessing;
};

struct RptPxDistanceJointDesc
{
    uint32_t struct_size;
    uint64_t actor0;
    uint64_t actor1;
    RptPxPose local_pose0;
    RptPxPose local_pose1;
    float minimum_distance;
    float maximum_distance;
    float stiffness;
    float damping;
    float tolerance;
    float break_force;
    float break_torque;
    float mass_scale;
    float connected_mass_scale;
    uint32_t enable_preprocessing;
};

struct RptPxForceCommand
{
    uint32_t struct_size;
    uint32_t operation;
    uint64_t actor;
    RptPxVec3 force;
    RptPxVec3 position;
    uint32_t mode;
    uint32_t wake;
};

struct RptPxBodyState
{
    uint32_t struct_size;
    uint32_t sleeping;
    uint64_t actor;
    RptPxPose pose;
    RptPxVec3 linear_velocity;
    RptPxVec3 angular_velocity;
};

RPTPX_API int32_t RPTPX_CALL rptpx_get_abi(uint32_t* abi_version, uint32_t* physx_version);
RPTPX_API const char* RPTPX_CALL rptpx_get_last_error();
RPTPX_API int32_t RPTPX_CALL rptpx_unity_get_scene_from_actor(void* actor, void** unity_scene);
RPTPX_API int32_t RPTPX_CALL rptpx_unity_get_scene_settings(void* unity_scene, RptPxUnitySceneSettings* settings);
RPTPX_API int32_t RPTPX_CALL rptpx_unity_enumerate_shapes(void* unity_scene, void** shapes, uint32_t capacity, uint32_t* written_or_required);
RPTPX_API int32_t RPTPX_CALL rptpx_unity_overlap_shapes(void* unity_scene, const RptPxVec3* point0, const RptPxVec3* point1, float radius, void** shapes, uint32_t capacity, uint32_t* written_or_required);
RPTPX_API int32_t RPTPX_CALL rptpx_unity_extract_mesh(void* shape, RptPxUnityMeshInfo* info, RptPxVec3* vertices, uint32_t vertex_capacity, uint32_t* indices, uint32_t index_capacity);
RPTPX_API int32_t RPTPX_CALL rptpx_unity_extract_heightfield(void* shape, RptPxUnityHeightFieldInfo* info, uint8_t* samples, uint32_t sample_capacity, RptPxMaterialDesc* materials, uint32_t material_capacity);
RPTPX_API int32_t RPTPX_CALL rptpx_create_scene(const RptPxSceneDesc* desc, void** scene);
RPTPX_API void RPTPX_CALL rptpx_destroy_scene(void* scene);
RPTPX_API int32_t RPTPX_CALL rptpx_create_material(void* scene, const RptPxMaterialDesc* desc, uint64_t* material);
RPTPX_API int32_t RPTPX_CALL rptpx_create_mesh(void* scene, const RptPxMeshDesc* desc, uint64_t* mesh);
RPTPX_API int32_t RPTPX_CALL rptpx_create_mesh_from_cooked(void* scene, uint32_t convex, const uint8_t* data, uint32_t length, uint64_t* mesh);
// A non-null desc cooks once and replaces the scene's pending result. Pass null
// desc to copy that result without cooking again. Successful copy consumes it;
// insufficient capacity retains it. Calls must belong to one scene-owner transaction.
RPTPX_API int32_t RPTPX_CALL rptpx_cook_mesh(void* scene, const RptPxMeshDesc* desc, uint8_t* output, uint32_t capacity, uint32_t* required);
RPTPX_API int32_t RPTPX_CALL rptpx_create_heightfield(void* scene, const RptPxHeightFieldDesc* desc, uint64_t* heightfield);
RPTPX_API int32_t RPTPX_CALL rptpx_create_actor(void* scene, const RptPxBodyDesc* desc, uint64_t* actor);
RPTPX_API int32_t RPTPX_CALL rptpx_attach_shape(void* scene, uint64_t actor, const RptPxShapeDesc* desc, uint64_t* shape);
RPTPX_API int32_t RPTPX_CALL rptpx_attach_shape_materials(void* scene, uint64_t actor, const RptPxShapeDesc* desc, const uint64_t* materials, uint32_t material_count, uint64_t* shape);
RPTPX_API int32_t RPTPX_CALL rptpx_update_mass_and_inertia(void* scene, uint64_t actor, float mass, RptPxVec3* center_of_mass);
RPTPX_API int32_t RPTPX_CALL rptpx_destroy_shape(void* scene, uint64_t shape);
RPTPX_API int32_t RPTPX_CALL rptpx_destroy_mesh(void* scene, uint64_t mesh);
RPTPX_API int32_t RPTPX_CALL rptpx_destroy_actor(void* scene, uint64_t actor);
RPTPX_API int32_t RPTPX_CALL rptpx_destroy_joint(void* scene, uint64_t joint);
RPTPX_API int32_t RPTPX_CALL rptpx_get_joint_broken(void* scene, uint64_t joint, uint32_t* broken);
RPTPX_API int32_t RPTPX_CALL rptpx_set_actor_pose(void* scene, uint64_t actor, const RptPxPose* pose, uint32_t kinematic_target);
RPTPX_API int32_t RPTPX_CALL rptpx_set_actor_state(void* scene, uint64_t actor, const RptPxBodyState* state, uint32_t kinematic, uint32_t wake);
RPTPX_API int32_t RPTPX_CALL rptpx_set_shape_enabled(void* scene, uint64_t shape, uint32_t enabled);
RPTPX_API int32_t RPTPX_CALL rptpx_ignore_shape_pair(void* scene, uint64_t shape0, uint64_t shape1);
RPTPX_API int32_t RPTPX_CALL rptpx_create_d6_joint(void* scene, const RptPxD6JointDesc* desc, uint64_t* joint);
RPTPX_API int32_t RPTPX_CALL rptpx_create_fixed_joint(void* scene, const RptPxFixedJointDesc* desc, uint64_t* joint);
RPTPX_API int32_t RPTPX_CALL rptpx_create_distance_joint(void* scene, const RptPxDistanceJointDesc* desc, uint64_t* joint);
RPTPX_API int32_t RPTPX_CALL rptpx_apply_forces(void* scene, const RptPxForceCommand* commands, uint32_t count);
RPTPX_API int32_t RPTPX_CALL rptpx_step(void* scene, float delta_time, RptPxBodyState* states, uint32_t capacity, uint32_t* written_or_required);
