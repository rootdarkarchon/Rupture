void test_corpse_drag(float dt)
{
    RptPxSceneDesc desc{};
    desc.struct_size = sizeof(desc);
    void* scene{};
    check(rptpx_create_scene(&desc, &scene) == RPTPX_OK, "corpse drag isolated scene");
    uint64_t hand{}, chest{}, joint{}, material{}, chest_shape{};
    auto hand_desc = body(RPTPX_ACTOR_KINEMATIC, 0.f, 0.f, 0.f);
    auto chest_desc = body(RPTPX_ACTOR_DYNAMIC, 0.f, 0.f, 0.f);
    hand_desc.flags = chest_desc.flags = 2; // no gravity
    chest_desc.mass = 80.f;
    RptPxMaterialDesc material_desc{};
    material_desc.struct_size = sizeof(material_desc);
    check(rptpx_create_material(scene, &material_desc, &material) == RPTPX_OK &&
        rptpx_create_actor(scene, &hand_desc, &hand) == RPTPX_OK &&
        rptpx_create_actor(scene, &chest_desc, &chest) == RPTPX_OK,
        "corpse drag shape-free hand and massive chest actors");
    auto chest_geometry = box_shape(material, .2f, .3f, .1f, 1);
    check(rptpx_attach_shape(scene, chest, &chest_geometry, &chest_shape) == RPTPX_OK, "corpse drag only chest has a collision shape");
    RptPxDistanceJointDesc tether{};
    tether.struct_size = sizeof(tether); tether.actor0 = hand; tether.actor1 = chest;
    tether.local_pose0 = tether.local_pose1 = pose(0.f, 0.f, 0.f);
    tether.maximum_distance = .05f; tether.tolerance = .001f;
    tether.break_force = tether.break_torque = 3.402823466e+38f;
    tether.mass_scale = tether.connected_mass_scale = 1.f;
    check(rptpx_create_distance_joint(scene, &tether, &joint) == RPTPX_OK, "corpse drag zero-spring radial tether");
    std::vector<RptPxBodyState> states;
    bool driven = true;
    float maximum_error = 0.f;
    const int steps = int(2.f / dt);
    for (int index = 1; index <= steps; ++index)
    {
        const auto target = pose(1.5f * dt * index, 0.f, 0.f);
        driven &= rptpx_set_actor_pose(scene, hand, &target, 1) == RPTPX_OK && step(scene, dt, states);
        const auto torso = find(states, chest), grip = find(states, hand);
        maximum_error = std::max(maximum_error, std::abs(grip.pose.position.x - torso.pose.position.x));
    }
    check(driven && maximum_error < .07f && find(states, chest).pose.position.x > 2.8f,
        "corpse drag kinematic targets pull massive chest within slack at graphical/headless cadence");

    RptPxForceCommand torque{};
    torque.struct_size = sizeof(torque); torque.operation = RPTPX_ADD_TORQUE;
    torque.actor = chest; torque.force = { 2.f, 0.f, 0.f }; torque.mode = RPTPX_IMPULSE; torque.wake = 1;
    check(rptpx_apply_forces(scene, &torque, 1) == RPTPX_OK, "corpse drag angular impulse during tether");
    for (int index = 0; index < steps / 2; ++index) driven &= step(scene, dt, states);
    check(driven && std::abs(find(states, chest).pose.rotation.x) > .1f, "corpse drag tether leaves angular motion free");

    auto torso = find(states, chest), grip = find(states, hand);
    const float old_x = torso.pose.position.x;
    torso.pose.position.x += 10.f; grip.pose.position.x += 10.f;
    torso.linear_velocity = torso.angular_velocity = grip.linear_velocity = grip.angular_velocity = { 0.f, 0.f, 0.f };
    torso.sleeping = grip.sleeping = 0;
    check(rptpx_set_actor_state(scene, chest, &torso, 0, 1) == RPTPX_OK &&
        rptpx_set_actor_state(scene, hand, &grip, 1, 0) == RPTPX_OK && step(scene, dt, states),
        "corpse drag common-offset state replacement");
    check(std::abs(find(states, chest).pose.position.x - old_x - 10.f) < .02f &&
        std::abs(find(states, chest).linear_velocity.x) < .1f,
        "corpse drag recovery preserves relative tether pose without launch velocity");
    check(rptpx_destroy_joint(scene, joint) == RPTPX_OK && rptpx_destroy_actor(scene, hand) == RPTPX_OK,
        "corpse drag grip disposal leaves chest actor alive");

    uint64_t light{};
    auto light_desc = body(RPTPX_ACTOR_DYNAMIC, 30.f, 0.f, 0.f); light_desc.flags = 2;
    check(rptpx_create_actor(scene, &light_desc, &light) == RPTPX_OK, "corpse drag assistance comparison actor");
    torso = find(states, chest); torso.linear_velocity = torso.angular_velocity = { 0.f, 0.f, 0.f };
    check(rptpx_set_actor_state(scene, chest, &torso, 0, 1) == RPTPX_OK, "corpse drag release velocity baseline");
    RptPxForceCommand assistance[2]{};
    for (int index = 0; index < 2; ++index)
    {
        assistance[index].struct_size = sizeof(RptPxForceCommand); assistance[index].operation = RPTPX_ADD_FORCE;
        assistance[index].actor = index ? light : chest; assistance[index].force = { 0.f, 10.f, 0.f };
        assistance[index].mode = RPTPX_ACCELERATION; assistance[index].wake = 1;
    }
    check(rptpx_apply_forces(scene, assistance, 2) == RPTPX_OK && step(scene, dt, states) &&
        std::abs(find(states, chest).linear_velocity.y - 10.f * dt) < .001f &&
        std::abs(find(states, light).linear_velocity.y - 10.f * dt) < .001f,
        "corpse drag COM acceleration is mass independent and integrated once by dt");
    check(rptpx_destroy_actor(scene, chest) == RPTPX_OK && rptpx_destroy_actor(scene, light) == RPTPX_OK,
        "corpse drag actors dispose after independent release");
    rptpx_destroy_scene(scene);
}
