#include "rpt_physx_bridge.h"

#include <cmath>
#include <cstdint>
#include <iostream>
#include <vector>
#include <chrono>
#include <cstdlib>
#include <algorithm>

namespace
{
int failures{};

void check(bool condition, const char* name)
{
    if (condition) std::cout << "PASS: " << name << '\n';
    else { std::cerr << "FAIL: " << name << " error=" << rptpx_get_last_error() << '\n'; ++failures; }
}

RptPxPose pose(float x, float y, float z)
{
    return { { x, y, z }, { 0.f, 0.f, 0.f, 1.f } };
}

RptPxQuat axis_angle_x(float radians)
{
	const float half = radians * 0.5f;
	return { std::sin(half), 0.f, 0.f, std::cos(half) };
}

float signed_x_angle(const RptPxQuat& rotation)
{
	float angle = 2.f * std::atan2(rotation.x, rotation.w);
	while (angle > 3.14159265f) angle -= 6.28318531f;
	while (angle < -3.14159265f) angle += 6.28318531f;
	return angle;
}

RptPxBodyDesc body(uint32_t kind, float x, float y, float z)
{
    RptPxBodyDesc value{};
    value.struct_size = sizeof(value);
    value.kind = kind;
    value.pose = pose(x, y, z);
    value.mass = 1.f;
    value.sleep_threshold = 0.005f;
    value.max_angular_velocity = 100.f;
    value.max_depenetration_velocity = 100.f;
    value.solver_position_iterations = 6;
    value.solver_velocity_iterations = 1;
    value.inertia_tensor = { 1.f, 1.f, 1.f };
    value.inertia_rotation = { 0.f, 0.f, 0.f, 1.f };
    return value;
}

RptPxShapeDesc box_shape(uint64_t material, float x, float y, float z, uint32_t shape_id)
{
    RptPxShapeDesc value{};
    value.struct_size = sizeof(value);
    value.geometry_kind = RPTPX_GEOMETRY_BOX;
    value.material_handle = material;
    value.local_pose = pose(0.f, 0.f, 0.f);
    value.dimensions = { x, y, z };
    value.mesh_scale = { 1.f, 1.f, 1.f };
    value.layer_bits = 1;
    value.collides_with_bits = 1;
    value.shape_id = shape_id;
    value.contact_offset = 0.01f;
    value.enabled = 1;
    return value;
}

RptPxBodyState find(const std::vector<RptPxBodyState>& states, uint64_t actor)
{
    for (const auto& state : states) if (state.actor == actor) return state;
    return {};
}

bool step(void* scene, float dt, std::vector<RptPxBodyState>& states)
{
    uint32_t count{};
    states.resize(32);
    int32_t result = rptpx_step(scene, dt, states.data(), static_cast<uint32_t>(states.size()), &count);
    if (result == RPTPX_BUFFER_TOO_SMALL)
    {
        states.resize(count);
        result = rptpx_step(scene, dt, states.data(), count, &count);
    }
    states.resize(count);
    return result == RPTPX_OK;
}

void test_ccd(bool triangles, bool ignored, bool transition)
{
    RptPxSceneDesc desc{};
    desc.struct_size = sizeof(desc);
    desc.scene_flags = 1u << 1;
    desc.bounce_threshold_velocity = 2.f;
    void* scene{};
    check(rptpx_create_scene(&desc, &scene) == RPTPX_OK, "CCD isolated scene");
    RptPxMaterialDesc material_desc{};
    material_desc.struct_size = sizeof(material_desc);
    uint64_t material{}, wall{}, moving{}, wall_shape{}, moving_shape{}, mesh_handle{};
    check(rptpx_create_material(scene, &material_desc, &material) == RPTPX_OK, "CCD material");
    auto wall_body = body(RPTPX_ACTOR_STATIC, 0.f, 0.f, 0.f);
    auto moving_body = body(RPTPX_ACTOR_DYNAMIC, -0.51f, 0.f, 0.f);
    moving_body.flags = 3; // CCD, no gravity
    moving_body.linear_velocity = { 30.f, 0.f, 0.f };
    auto wall_geometry = box_shape(material, 0.01f, 2.f, 2.f, 1);
    if (triangles)
    {
        const RptPxVec3 vertices[] = { {0.f,-2.f,-2.f}, {0.f,2.f,-2.f}, {0.f,2.f,2.f}, {0.f,-2.f,2.f} };
        const uint32_t indices[] = { 0, 2, 1, 0, 3, 2 };
        RptPxMeshDesc mesh{};
        mesh.struct_size = sizeof(mesh); mesh.vertices = vertices; mesh.vertex_count = 4;
        mesh.indices = indices; mesh.index_count = 6;
        check(rptpx_create_mesh(scene, &mesh, &mesh_handle) == RPTPX_OK, "CCD thin triangle wall");
        wall_geometry.geometry_kind = RPTPX_GEOMETRY_TRIANGLE_MESH;
        wall_geometry.mesh_handle = mesh_handle;
    }
    auto moving_geometry = box_shape(material, .025f, .025f, .025f, 2);
    check(rptpx_create_actor(scene, &wall_body, &wall) == RPTPX_OK &&
        rptpx_attach_shape(scene, wall, &wall_geometry, &wall_shape) == RPTPX_OK &&
        rptpx_create_actor(scene, &moving_body, &moving) == RPTPX_OK &&
        rptpx_attach_shape(scene, moving, &moving_geometry, &moving_shape) == RPTPX_OK, "CCD actors and contact shapes");
    if (ignored) check(rptpx_ignore_shape_pair(scene, wall_shape, moving_shape) == RPTPX_OK, "CCD exact exclusion");
    if (transition)
    {
        RptPxBodyState state{}; state.struct_size = sizeof(state); state.actor = moving;
        state.pose = moving_body.pose; state.linear_velocity = moving_body.linear_velocity;
        check(rptpx_set_actor_state(scene, moving, &state, 1, 0) == RPTPX_OK &&
            rptpx_set_actor_state(scene, moving, &state, 0, 1) == RPTPX_OK, "CCD kinematic handoff");
    }
    std::vector<RptPxBodyState> states;
    bool stepped = true;
    for (int index = 0; index < 6; index++) stepped &= step(scene, 1.f / 30.f, states);
    float x = find(states, moving).pose.position.x;
    check(stepped && (ignored ? x > 1.f : x < 0.f && x > -.51f),
        ignored ? "CCD respects explicitly ignored contact pairs" : triangles ? "30 m/s gib cannot tunnel through triangle wall" : "30 m/s gib cannot tunnel through thin box wall");
    rptpx_destroy_scene(scene);
}

void test_corpse_floor(bool ccd, bool triangles, float dt, bool transition)
{
    RptPxSceneDesc desc{};
    desc.struct_size = sizeof(desc);
    desc.scene_flags = 1u << 1;
    desc.gravity = { 0.f, -9.81f, 0.f };
    desc.bounce_threshold_velocity = 2.f;
    void* scene{};
    check(rptpx_create_scene(&desc, &scene) == RPTPX_OK, "corpse floor scene");
    RptPxMaterialDesc material_desc{};
    material_desc.struct_size = sizeof(material_desc);
    uint64_t material{}, floor{}, falling{}, floor_shape{}, falling_shape{}, mesh_handle{};
    check(rptpx_create_material(scene, &material_desc, &material) == RPTPX_OK, "corpse floor material");
    auto floor_body = body(RPTPX_ACTOR_STATIC, 0.f, 0.f, 0.f);
    auto falling_body = body(RPTPX_ACTOR_DYNAMIC, 0.f, .18f, 0.f);
    falling_body.flags = ccd ? 1u : 0u;
    falling_body.linear_velocity = { 0.f, -2.f, 0.f };
    auto floor_geometry = box_shape(material, 2.f, .01f, 2.f, 1);
    if (triangles)
    {
        const RptPxVec3 vertices[] = { {-2.f,0.f,-2.f}, {-2.f,0.f,2.f}, {2.f,0.f,2.f}, {2.f,0.f,-2.f} };
        const uint32_t indices[] = { 0, 1, 2, 0, 2, 3 };
        RptPxMeshDesc mesh{};
        mesh.struct_size = sizeof(mesh); mesh.vertices = vertices; mesh.vertex_count = 4;
        mesh.indices = indices; mesh.index_count = 6;
        check(rptpx_create_mesh(scene, &mesh, &mesh_handle) == RPTPX_OK, "corpse triangle floor");
        floor_geometry.geometry_kind = RPTPX_GEOMETRY_TRIANGLE_MESH;
        floor_geometry.mesh_handle = mesh_handle;
    }
    auto falling_geometry = box_shape(material, .08f, .08f, .08f, 2);
    check(rptpx_create_actor(scene, &floor_body, &floor) == RPTPX_OK &&
        rptpx_attach_shape(scene, floor, &floor_geometry, &floor_shape) == RPTPX_OK &&
        rptpx_create_actor(scene, &falling_body, &falling) == RPTPX_OK &&
        rptpx_attach_shape(scene, falling, &falling_geometry, &falling_shape) == RPTPX_OK, "corpse floor actors");
    if (transition)
    {
        RptPxBodyState state{}; state.struct_size = sizeof(state); state.actor = falling;
        state.pose = falling_body.pose; state.linear_velocity = falling_body.linear_velocity;
        check(rptpx_set_actor_state(scene, falling, &state, 1, 0) == RPTPX_OK &&
            rptpx_set_actor_state(scene, falling, &state, 0, 1) == RPTPX_OK, "corpse sleep/wake handoff");
    }
    std::vector<RptPxBodyState> states;
    bool stepped = true;
    float minimum_y = falling_body.pose.position.y;
    for (int index = 0; index < 30; index++)
    {
        stepped &= step(scene, dt, states);
        const float y = find(states, falling).pose.position.y;
        if (y < minimum_y) minimum_y = y;
    }
    std::cout << "Corpse floor: ccd=" << ccd << " triangles=" << triangles
        << " dt=" << dt << " wake=" << transition << " minimumY=" << minimum_y << '\n';
    check(stepped && (ccd ? minimum_y > .06f : minimum_y < -1.f),
        ccd ? "corpse CCD prevents floor tunneling at authority cadence" : "discrete authority corpse reproduces floor tunneling");
    rptpx_destroy_scene(scene);
}

void test_handle_churn(void* scene, uint64_t material)
{
    std::vector<uint64_t> actors(5000), shapes(5000);
    auto geometry = box_shape(material, .01f, .01f, .01f, 0);
    bool valid = true;
    for (int cycle = 0; cycle < 3; cycle++)
    {
        for (size_t index = 0; index < actors.size(); index++)
        {
            auto desc = body(RPTPX_ACTOR_STATIC, static_cast<float>(index), 100.f, 0.f);
            uint64_t old_actor = actors[index], old_shape = shapes[index];
            valid &= rptpx_create_actor(scene, &desc, &actors[index]) == RPTPX_OK;
            valid &= rptpx_attach_shape(scene, actors[index], &geometry, &shapes[index]) == RPTPX_OK;
            if (cycle != 0)
                valid &= rptpx_destroy_actor(scene, old_actor) == RPTPX_INVALID_HANDLE &&
                    rptpx_set_shape_enabled(scene, old_shape, 1) == RPTPX_INVALID_HANDLE;
        }
        for (uint64_t actor : actors) valid &= rptpx_destroy_actor(scene, actor) == RPTPX_OK;
    }
    check(valid, "5000 actors with attached shapes survive slot reuse without accepting stale handles");
}
}


void test_lzma()
{
    // Independently encoded raw LZMA1 fixture, including embedded NUL bytes.
    const uint8_t compressed[] = { 0x5d, 0x0, 0x0, 0x10, 0x0, 0x0, 0x29, 0x1d, 0x4a, 0x7, 0x6f, 0xe8, 0x1, 0xc5, 0xeb, 0xc1, 0x96, 0xf1, 0xe2, 0x2a, 0x69, 0x1c, 0xd0, 0xb8, 0xa3, 0x16, 0x1e, 0xd1, 0x47, 0x7d, 0xaa, 0x10, 0xb0, 0xd4, 0xcb, 0x5d, 0x4b, 0x25, 0x2e, 0xd4, 0x53, 0xff, 0xff, 0xf6, 0x9a, 0xc0, 0x0 };
    const char text[] = "Rupture clothing mesh data";
    const uint32_t size = sizeof(text) * 16;
    std::vector<uint8_t> output(size + 2, 0xa5);
    check(rptpx_lzma_decode_v1(compressed, sizeof(compressed), output.data() + 1, size) == RPTPX_OK,
        "LZMA decoder works without a PhysX scene");
    bool exact = output.front() == 0xa5 && output.back() == 0xa5;
    for (uint32_t i = 0; i < size; ++i) exact &= output[i + 1] == static_cast<uint8_t>(text[i % sizeof(text)]);
    check(exact, "LZMA output matches fixture and preserves buffer guards");
    check(rptpx_lzma_decode_v1(compressed, sizeof(compressed) - 3, output.data() + 1, size) != RPTPX_OK,
        "truncated LZMA is rejected");
    check(rptpx_lzma_decode_v1(compressed, sizeof(compressed), output.data() + 1, size - 1) != RPTPX_OK,
        "LZMA output size mismatch is rejected");
    auto invalid = std::vector<uint8_t>(compressed, compressed + sizeof(compressed));
    invalid[0] = 0xff;
    check(rptpx_lzma_decode_v1(invalid.data(), static_cast<uint32_t>(invalid.size()), output.data() + 1, size) != RPTPX_OK,
        "invalid LZMA properties are rejected");
    check(rptpx_lzma_decode_v1(nullptr, 0, output.data(), size) != RPTPX_OK &&
        rptpx_lzma_decode_v1(compressed, sizeof(compressed), nullptr, size) != RPTPX_OK &&
        rptpx_lzma_decode_v1(compressed, 4, output.data(), size) != RPTPX_OK,
        "invalid LZMA arguments are rejected");
    check(output.front() == 0xa5 && output.back() == 0xa5, "LZMA rejection preserves buffer guards");
}

#include "rpt_casing_tests.inl"

#include "rpt_corpse_drag_tests.inl"

int main(int argc, char** argv)
{
    if (argc >= 3 && std::string(argv[1]) == "--casing-mixed")
    {
        benchmark_casing_mixed(uint32_t(std::atoi(argv[2])), argc > 3 ? float(std::atof(argv[3])) : 1.f/60);
        return failures ? 1 : 0;
    }
    if (argc == 3 && std::string(argv[1]) == "--casing-fire")
    {
        benchmark_casing_fire(uint32_t(std::atoi(argv[2])));
        return failures ? 1 : 0;
    }
    if (argc == 5 && std::string(argv[1]) == "--casings")
    {
        test_casings(uint32_t(std::atoi(argv[2])), std::atoi(argv[3]) != 0, std::atoi(argv[4]) != 0, true);
        return failures ? 1 : 0;
    }
    std::cout << std::unitbuf;
    test_lzma();
    test_corpse_drag(1.f / 60.f);
    test_corpse_drag(.1f);
    uint32_t abi{}, physx{};
    check(rptpx_get_abi(&abi, &physx) == RPTPX_OK && abi == RPTPX_ABI_VERSION && physx == RPTPX_PHYSX_VERSION, "native ABI and pinned PhysX version");
	void* unity_scene{};
	RptPxUnitySceneSettings unity_scene_settings{};
	unity_scene_settings.struct_size = sizeof(unity_scene_settings);
	uint32_t unity_shapes{};
	RptPxUnityMeshInfo unity_mesh{};
	unity_mesh.struct_size = sizeof(unity_mesh);
	RptPxUnityHeightFieldInfo unity_heightfield{};
	unity_heightfield.struct_size = sizeof(unity_heightfield);
	check(rptpx_unity_get_scene_from_actor(nullptr, &unity_scene) == RPTPX_INVALID_ARGUMENT &&
		rptpx_unity_get_scene_settings(nullptr, &unity_scene_settings) == RPTPX_INVALID_ARGUMENT &&
		rptpx_unity_enumerate_shapes(nullptr, nullptr, 0, &unity_shapes) == RPTPX_INVALID_ARGUMENT &&
		rptpx_unity_overlap_shapes(nullptr, nullptr, nullptr, 0.f, nullptr, 0, &unity_shapes) == RPTPX_INVALID_ARGUMENT &&
		rptpx_unity_extract_mesh(nullptr, &unity_mesh, nullptr, 0, nullptr, 0) == RPTPX_INVALID_ARGUMENT &&
		rptpx_unity_extract_heightfield(nullptr, &unity_heightfield, nullptr, 0, nullptr, 0) == RPTPX_INVALID_ARGUMENT,
		"Unity PhysX extraction rejects invalid pointers without dereferencing them");

    RptPxSceneDesc scene_desc{};
    scene_desc.struct_size = sizeof(scene_desc);
    scene_desc.dispatcher_threads = 2;
    scene_desc.gravity = { 0.f, -9.81f, 0.f };
    scene_desc.bounce_threshold_velocity = 2.f;
    scene_desc.solver_type = 0;
    scene_desc.friction_type = 0;
    scene_desc.scene_flags = (1u << 1) | (1u << 6) | (1u << 10);
    void* scene{};
    check(rptpx_create_scene(&scene_desc, &scene) == RPTPX_OK && scene, "scene lifetime create");

	RptPxSceneDesc invalid_scene_desc = scene_desc;
	invalid_scene_desc.solver_type = 2;
	void* invalid_scene{};
	check(rptpx_create_scene(&invalid_scene_desc, &invalid_scene) == RPTPX_INVALID_ARGUMENT && !invalid_scene,
		"scene creation rejects solver settings outside the source-scene ABI");

    RptPxMaterialDesc material_desc{};
    material_desc.struct_size = sizeof(material_desc);
    material_desc.static_friction = 0.6f;
    material_desc.dynamic_friction = 0.6f;
    uint64_t material{};
    check(rptpx_create_material(scene, &material_desc, &material) == RPTPX_OK && material, "material creation");

    uint64_t ground{};
    auto ground_desc = body(RPTPX_ACTOR_STATIC, 0.f, -0.5f, 0.f);
    check(rptpx_create_actor(scene, &ground_desc, &ground) == RPTPX_OK, "static primitive actor");
    uint64_t ground_shape{};
    auto ground_geometry = box_shape(material, 10.f, 0.5f, 10.f, 1);
    check(rptpx_attach_shape(scene, ground, &ground_geometry, &ground_shape) == RPTPX_OK, "box primitive shape");

    uint64_t dynamic{};
    auto dynamic_desc = body(RPTPX_ACTOR_DYNAMIC, 0.f, 2.f, 0.f);
    check(rptpx_create_actor(scene, &dynamic_desc, &dynamic) == RPTPX_OK, "dynamic primitive actor");
    uint64_t dynamic_shape{};
    auto dynamic_geometry = box_shape(material, 0.5f, 0.5f, 0.5f, 2);
    check(rptpx_attach_shape(scene, dynamic, &dynamic_geometry, &dynamic_shape) == RPTPX_OK, "dynamic box shape");

    std::vector<RptPxBodyState> states;
    bool stepped = true;
    for (int index = 0; index < 180; ++index) stepped &= step(scene, 1.f / 60.f, states);
    const auto resting = find(states, dynamic);
    check(stepped && resting.actor == dynamic && resting.pose.position.y > 0.45f && resting.pose.position.y < 0.65f, "simulate/fetchResults primitive collision");
    check(resting.sleeping != 0, "sleep publication");

    RptPxForceCommand force{};
    force.struct_size = sizeof(force);
    force.operation = RPTPX_ADD_FORCE;
    force.actor = dynamic;
    force.force = { 0.f, 2.f, 0.f };
    force.mode = RPTPX_IMPULSE;
    force.wake = 1;
    check(rptpx_apply_forces(scene, &force, 1) == RPTPX_OK && step(scene, 1.f / 60.f, states) && find(states, dynamic).linear_velocity.y > 0.f, "force and wake command");

	uint64_t prediction_transition{}, prediction_transition_shape{};
	auto prediction_transition_desc = body(RPTPX_ACTOR_DYNAMIC, 6.f, 3.f, 0.f);
	prediction_transition_desc.flags = 2;
	check(rptpx_create_actor(scene, &prediction_transition_desc, &prediction_transition) == RPTPX_OK &&
		rptpx_attach_shape(scene, prediction_transition, &dynamic_geometry, &prediction_transition_shape) == RPTPX_OK,
		"prediction ownership transition actor");
	RptPxForceCommand interrupted_impulse{};
	interrupted_impulse.struct_size = sizeof(interrupted_impulse);
	interrupted_impulse.operation = RPTPX_ADD_FORCE_AT_POSITION;
	interrupted_impulse.actor = prediction_transition;
	interrupted_impulse.force = { 12.f, 0.f, 0.f };
	interrupted_impulse.position = { 6.f, 3.5f, 0.f };
	interrupted_impulse.mode = RPTPX_IMPULSE;
	interrupted_impulse.wake = 1;
	RptPxBodyState kinematic_state{};
	kinematic_state.struct_size = sizeof(kinematic_state);
	kinematic_state.actor = prediction_transition;
	kinematic_state.pose = pose(6.f, 3.f, 0.f);
	RptPxBodyState dynamic_state = kinematic_state;
	check(rptpx_apply_forces(scene, &interrupted_impulse, 1) == RPTPX_OK &&
		rptpx_set_actor_state(scene, prediction_transition, &kinematic_state, 1, 0) == RPTPX_OK &&
		rptpx_set_actor_state(scene, prediction_transition, &dynamic_state, 0, 1) == RPTPX_OK &&
		step(scene, 1.f / 60.f, states) &&
		std::abs(find(states, prediction_transition).pose.position.x - 6.f) < 0.001f &&
		std::abs(find(states, prediction_transition).linear_velocity.x) < 0.001f &&
		std::abs(find(states, prediction_transition).angular_velocity.z) < 0.001f,
		"kinematic ownership transition discards interrupted impulse and torque");
	check(rptpx_destroy_actor(scene, prediction_transition) == RPTPX_OK, "prediction ownership transition cleanup");

    uint64_t severed_placeholder{};
    auto severed_desc = body(RPTPX_ACTOR_KINEMATIC, 50.f, 2.f, 0.f);
    check(rptpx_create_actor(scene, &severed_desc, &severed_placeholder) == RPTPX_OK,
        "severed corpse pose actor requires no collision shapes");
    auto severed_pose = pose(51.f, 3.f, 0.f);
    check(rptpx_set_actor_pose(scene, severed_placeholder, &severed_pose, 0) == RPTPX_OK &&
        step(scene, 1.f / 60.f, states) && find(states, severed_placeholder).actor == severed_placeholder &&
        std::abs(find(states, severed_placeholder).pose.position.y - 3.f) < 0.001f,
        "shape-free severed pose actor remains addressable and stationary under gravity");
    check(rptpx_destroy_actor(scene, severed_placeholder) == RPTPX_OK,
        "shape-free severed corpse pose actor cleanup");

    uint64_t kinematic{};
    auto kinematic_desc = body(RPTPX_ACTOR_KINEMATIC, 3.f, 0.5f, 0.f);
    check(rptpx_create_actor(scene, &kinematic_desc, &kinematic) == RPTPX_OK, "kinematic movable actor");
    uint64_t kinematic_shape{};
    auto kinematic_geometry = box_shape(material, 0.5f, 0.5f, 0.5f, 3);
    check(rptpx_attach_shape(scene, kinematic, &kinematic_geometry, &kinematic_shape) == RPTPX_OK, "kinematic movable shape");
    auto target = pose(4.f, 0.5f, 0.f);
    check(rptpx_set_actor_pose(scene, kinematic, &target, 1) == RPTPX_OK && step(scene, 1.f / 60.f, states) && find(states, kinematic).pose.position.x > 3.9f, "movable actor propagation");

    uint64_t joined{};
    auto joined_desc = body(RPTPX_ACTOR_DYNAMIC, 0.f, 1.5f, 0.f);
    check(rptpx_create_actor(scene, &joined_desc, &joined) == RPTPX_OK, "joint child actor");
    uint64_t joined_shape{};
    check(rptpx_attach_shape(scene, joined, &dynamic_geometry, &joined_shape) == RPTPX_OK, "joint child shape");
    RptPxFixedJointDesc fixed{};
    fixed.struct_size = sizeof(fixed);
    fixed.actor0 = joined;
    fixed.actor1 = dynamic;
    fixed.local_pose0 = pose(0.f, -0.5f, 0.f);
    fixed.local_pose1 = pose(0.f, 0.5f, 0.f);
    uint64_t joint{};
    check(rptpx_create_fixed_joint(scene, &fixed, &joint) == RPTPX_OK && joint, "fixed joint creation");
	uint32_t joint_broken{ 1 };
	check(rptpx_get_joint_broken(scene, joint, &joint_broken) == RPTPX_OK && joint_broken == 0, "joint break-state publication");
    check(rptpx_destroy_joint(scene, joint) == RPTPX_OK, "joint destruction");
	check(rptpx_get_joint_broken(scene, joint, &joint_broken) == RPTPX_INVALID_HANDLE, "stale joint handle rejection");

	uint64_t limit_parent{}, limit_child{}, limit_shape{};
	auto limit_parent_desc = body(RPTPX_ACTOR_STATIC, 12.f, 2.f, 0.f);
	auto limit_child_desc = body(RPTPX_ACTOR_DYNAMIC, 12.f, 2.f, 0.f);
	limit_child_desc.flags = 2;
	limit_child_desc.pose.rotation = axis_angle_x(1.57079633f);
	check(rptpx_create_actor(scene, &limit_parent_desc, &limit_parent) == RPTPX_OK &&
		rptpx_create_actor(scene, &limit_child_desc, &limit_child) == RPTPX_OK &&
		rptpx_attach_shape(scene, limit_child, &dynamic_geometry, &limit_shape) == RPTPX_OK,
		"asymmetric D6 limit actors");
	RptPxD6JointDesc d6{};
	d6.struct_size = sizeof(d6);
	d6.actor0 = limit_child;
	d6.actor1 = limit_parent;
	d6.local_pose0 = pose(0.f, 0.f, 0.f);
	d6.local_pose1 = pose(0.f, 0.f, 0.f);
	d6.twist_lower = -0.17453293f;
	d6.twist_upper = 0.34906585f;
	d6.swing_y = 0.f;
	d6.swing_z = 0.f;
	d6.projection = 1;
	d6.projection_linear_tolerance = 0.025f;
	d6.projection_angular_tolerance = 0.08726646f;
	d6.mass_scale = 1.f;
	d6.connected_mass_scale = 1.f;
	d6.enable_preprocessing = 1;
	uint64_t d6_joint{};
	bool d6_stepped = rptpx_create_d6_joint(scene, &d6, &d6_joint) == RPTPX_OK && d6_joint;
	for (int index = 0; index < 30 && d6_stepped; ++index) d6_stepped &= step(scene, 1.f / 60.f, states);
	const float constrained_angle = signed_x_angle(find(states, limit_child).pose.rotation);
	check(d6_stepped && constrained_angle >= -0.22f && constrained_angle <= 0.44f,
		"asymmetric D6 twist limit corrects an inverted hinge pose");
	check(rptpx_destroy_joint(scene, d6_joint) == RPTPX_OK && rptpx_destroy_actor(scene, limit_child) == RPTPX_OK &&
		rptpx_destroy_actor(scene, limit_parent) == RPTPX_OK, "asymmetric D6 limit cleanup");

	uint64_t ignored_actor0{}, ignored_actor1{}, ignored_shape0{}, ignored_shape1{};
	auto ignored_body0 = body(RPTPX_ACTOR_DYNAMIC, 7.f, 2.f, 0.f);
	auto ignored_body1 = body(RPTPX_ACTOR_DYNAMIC, 7.2f, 2.f, 0.f);
	ignored_body0.flags = 2;
	ignored_body1.flags = 2;
	auto ignored_geometry0 = box_shape(material, 0.5f, 0.5f, 0.5f, 40);
	auto ignored_geometry1 = box_shape(material, 0.5f, 0.5f, 0.5f, 41);
	ignored_geometry0.owner_group = 7;
	ignored_geometry1.owner_group = 7;
	check(rptpx_create_actor(scene, &ignored_body0, &ignored_actor0) == RPTPX_OK &&
		rptpx_create_actor(scene, &ignored_body1, &ignored_actor1) == RPTPX_OK &&
		rptpx_attach_shape(scene, ignored_actor0, &ignored_geometry0, &ignored_shape0) == RPTPX_OK &&
		rptpx_attach_shape(scene, ignored_actor1, &ignored_geometry1, &ignored_shape1) == RPTPX_OK,
		"exact ignored-pair actors and shapes");
	check(rptpx_ignore_shape_pair(scene, ignored_shape0, ignored_shape1) == RPTPX_OK && step(scene, 1.f / 60.f, states) &&
		std::abs(find(states, ignored_actor1).pose.position.x - find(states, ignored_actor0).pose.position.x) < 0.25f,
		"exact ignored-pair filtering without blanket same-owner suppression");

    const RptPxVec3 vertices[] = { { -1.f, 0.f, -1.f }, { 1.f, 0.f, -1.f }, { 0.f, 0.f, 1.f }, { 0.f, 1.f, 0.f } };
    const uint32_t triangles[] = { 0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3 };
    RptPxMeshDesc mesh{};
    mesh.struct_size = sizeof(mesh);
    mesh.vertices = vertices;
    mesh.vertex_count = 4;
    mesh.indices = triangles;
    mesh.index_count = 12;
    uint32_t cooked_size{};
    check(rptpx_cook_mesh(scene, &mesh, nullptr, 0, &cooked_size) == RPTPX_BUFFER_TOO_SMALL && cooked_size > 0, "triangle mesh cooking size");
    std::vector<uint8_t> cooked(cooked_size);
    check(rptpx_cook_mesh(scene, nullptr, cooked.data(), cooked_size - 1, &cooked_size) == RPTPX_BUFFER_TOO_SMALL, "cooked result survives insufficient copy capacity");
    check(rptpx_cook_mesh(scene, nullptr, cooked.data(), cooked_size, &cooked_size) == RPTPX_OK, "triangle result copied without a source descriptor or recooking");
    uint32_t consumed_size{};
    check(rptpx_cook_mesh(scene, nullptr, nullptr, 0, &consumed_size) == RPTPX_INVALID_ARGUMENT, "cooked result is consumed exactly once");
    uint64_t triangle_mesh{};
    check(rptpx_create_mesh_from_cooked(scene, 0, cooked.data(), cooked_size, &triangle_mesh) == RPTPX_OK, "triangle cooked mesh lifetime");
    mesh.convex = 1;
    check(rptpx_cook_mesh(scene, &mesh, nullptr, 0, &cooked_size) == RPTPX_BUFFER_TOO_SMALL && cooked_size > 0, "convex mesh cooking size");
	std::vector<uint8_t> convex_cooked(cooked_size);
	check(rptpx_cook_mesh(scene, nullptr, convex_cooked.data(), cooked_size, &cooked_size) == RPTPX_OK, "convex result copied without recooking");
	uint64_t convex_mesh{}, convex_actor{}, convex_shape{};
	check(rptpx_create_mesh_from_cooked(scene, 1, convex_cooked.data(), cooked_size, &convex_mesh) == RPTPX_OK, "convex cooked mesh lifetime");
	auto convex_actor_desc = body(RPTPX_ACTOR_DYNAMIC, 15.f, 2.f, 0.f);
	convex_actor_desc.flags = 2;
	auto convex_geometry = box_shape(material, 0.f, 0.f, 0.f, 70);
	convex_geometry.geometry_kind = RPTPX_GEOMETRY_CONVEX_MESH;
	convex_geometry.mesh_handle = convex_mesh;
	RptPxVec3 convex_center{};
	check(rptpx_create_actor(scene, &convex_actor_desc, &convex_actor) == RPTPX_OK &&
		rptpx_attach_shape(scene, convex_actor, &convex_geometry, &convex_shape) == RPTPX_OK &&
		rptpx_update_mass_and_inertia(scene, convex_actor, 2.f, &convex_center) == RPTPX_OK &&
		std::isfinite(convex_center.x) && std::isfinite(convex_center.y) && std::isfinite(convex_center.z),
		"convex actor derives finite center of mass and inertia from its cooked hull");
	check(rptpx_destroy_actor(scene, convex_actor) == RPTPX_OK, "convex mass-property actor cleanup");
	check(rptpx_destroy_mesh(scene, convex_mesh) == RPTPX_OK &&
		rptpx_destroy_mesh(scene, convex_mesh) == RPTPX_INVALID_HANDLE,
		"transient convex mesh cleanup and stale-handle rejection");

	struct HeightFieldSample
	{
		int16_t height;
		uint8_t material0;
		uint8_t material1;
	};
	static_assert(sizeof(HeightFieldSample) == 4, "PhysX heightfield test sample ABI");
	std::vector<HeightFieldSample> height_samples(9);
	for (size_t index = 0; index < height_samples.size(); ++index)
	{
		height_samples[index].height = 0;
		height_samples[index].material0 = static_cast<uint8_t>(index % 2);
		height_samples[index].material1 = static_cast<uint8_t>((index + 1) % 2);
	}
	height_samples[0].material0 = 127;
	height_samples[0].material1 = 127;
	RptPxHeightFieldDesc heightfield_desc{};
	heightfield_desc.struct_size = sizeof(heightfield_desc);
	heightfield_desc.rows = 3;
	heightfield_desc.columns = 3;
	heightfield_desc.sample_stride = sizeof(HeightFieldSample);
	heightfield_desc.samples = reinterpret_cast<const uint8_t*>(height_samples.data());
	heightfield_desc.sample_bytes = static_cast<uint32_t>(height_samples.size() * sizeof(HeightFieldSample));
	uint64_t heightfield{};
	check(rptpx_create_heightfield(scene, &heightfield_desc, &heightfield) == RPTPX_OK && heightfield,
		"heightfield sample resource creation");
	RptPxMaterialDesc terrain_material_desc = material_desc;
	terrain_material_desc.static_friction = 0.9f;
	terrain_material_desc.dynamic_friction = 0.8f;
	uint64_t terrain_material{};
	check(rptpx_create_material(scene, &terrain_material_desc, &terrain_material) == RPTPX_OK && terrain_material,
		"heightfield secondary material creation");
	uint64_t heightfield_actor{}, heightfield_shape{};
	auto heightfield_actor_desc = body(RPTPX_ACTOR_STATIC, 20.f, 0.f, 0.f);
	auto heightfield_geometry = box_shape(material, 0.01f, 2.f, 2.f, 80);
	heightfield_geometry.geometry_kind = RPTPX_GEOMETRY_HEIGHTFIELD;
	heightfield_geometry.mesh_handle = heightfield;
	const uint64_t terrain_materials[] = { material, terrain_material };
	check(rptpx_create_actor(scene, &heightfield_actor_desc, &heightfield_actor) == RPTPX_OK &&
		rptpx_attach_shape_materials(scene, heightfield_actor, &heightfield_geometry, terrain_materials, 2, &heightfield_shape) == RPTPX_OK,
		"multi-material heightfield shape attachment");
	uint64_t terrain_test_body{}, terrain_test_shape{}, terrain_hole_body{}, terrain_hole_shape{};
	auto terrain_test_desc = body(RPTPX_ACTOR_DYNAMIC, 23.f, 2.f, 1.f);
	auto terrain_hole_desc = body(RPTPX_ACTOR_DYNAMIC, 21.f, 2.f, 1.f);
	check(rptpx_create_actor(scene, &terrain_test_desc, &terrain_test_body) == RPTPX_OK &&
		rptpx_attach_shape(scene, terrain_test_body, &dynamic_geometry, &terrain_test_shape) == RPTPX_OK &&
		rptpx_create_actor(scene, &terrain_hole_desc, &terrain_hole_body) == RPTPX_OK &&
		rptpx_attach_shape(scene, terrain_hole_body, &dynamic_geometry, &terrain_hole_shape) == RPTPX_OK,
		"heightfield solid and hole collision test bodies");
	bool heightfield_stepped = true;
	for (int index = 0; index < 180; ++index) heightfield_stepped &= step(scene, 1.f / 60.f, states);
	const auto terrain_resting = find(states, terrain_test_body);
	check(heightfield_stepped && terrain_resting.actor == terrain_test_body && terrain_resting.pose.position.y > 0.45f && terrain_resting.pose.position.y < 0.65f,
		"dynamic body rests on recreated heightfield");
	check(find(states, terrain_hole_body).pose.position.y < -5.f, "dynamic body passes through preserved heightfield hole");
	check(rptpx_destroy_actor(scene, terrain_test_body) == RPTPX_OK && rptpx_destroy_actor(scene, terrain_hole_body) == RPTPX_OK &&
		rptpx_destroy_actor(scene, heightfield_actor) == RPTPX_OK,
		"heightfield actor cleanup");

    check(rptpx_destroy_actor(scene, joined) == RPTPX_OK, "actor destruction");
    check(rptpx_destroy_actor(scene, joined) == RPTPX_INVALID_HANDLE, "stale actor handle rejection");
    check(rptpx_set_shape_enabled(scene, joined_shape, 1) == RPTPX_INVALID_HANDLE, "stale shape handle rejection");

    test_handle_churn(scene, material);
    rptpx_destroy_scene(scene);
    test_ccd(false, false, false);
    test_ccd(true, false, false);
    test_ccd(false, true, false);
    test_ccd(false, false, true);
    for (bool triangles : { false, true })
    {
        test_corpse_floor(false, triangles, .1f, false);
        for (float dt : { 1.f / 30.f, .1f, 1.f / 3.f })
        {
            test_corpse_floor(true, triangles, dt, false);
            test_corpse_floor(true, triangles, dt, true);
        }
    }
    test_headless_casings();
    test_casing_contacts_and_occlusion();
    test_casing_supports();
    test_casings(16, false, false, false);
    test_casings(16, true, false, false);
    if (std::getenv("RPT_CASING_BENCHMARK"))
        for (uint32_t count : { 1024u, 4096u, 8192u }) for (bool pile : { false, true })
            for (bool box : { false, true }) if (!std::getenv("RPT_CASING_BOX_ONLY") || box) test_casings(count, box, pile, true);
    std::cout << (failures ? "FAILED" : "ALL PASS") << ": " << failures << " failure(s)\n";
    return failures ? 1 : 0;
}
