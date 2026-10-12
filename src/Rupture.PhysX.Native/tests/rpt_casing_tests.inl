static_assert(sizeof(RptPxCasingDesc) == 112 && sizeof(RptPxCasingImpact) == 56 &&
    sizeof(RptPxCasingState) == 56 && sizeof(RptPxCasingContact) == 40, "managed/native casing ABI");

// Mixed sleeping-population wake stress. Kept separate from the cheap default
// regression run; reports every step so owner stalls can be sampled externally.
void benchmark_casing_mixed(uint32_t population, float dt)
{
    RptPxSceneDesc d{}; d.struct_size = sizeof(d); d.dispatcher_threads = 2;
    d.graphical_casings = 1; d.scene_flags = 0x42; d.gravity = {0, -9.81f, 0};
    void* scene{}; check(rptpx_create_scene(&d, &scene) == 0, "mixed wake scene");
    RptPxMaterialDesc md{}; md.struct_size = sizeof(md); md.static_friction = .6f; md.dynamic_friction = .5f;
    uint64_t material{}, floor{}, shape{}; rptpx_create_material(scene, &md, &material);
    auto bd = body(0, 0, -.02f, 0); auto sd = box_shape(material, 20, .02f, 20, 1);
    rptpx_create_actor(scene, &bd, &floor); rptpx_attach_shape(scene, floor, &sd, &shape);
    std::vector<RptPxCasingDesc> casings(population);
    for (uint32_t i = 0; i < population; ++i)
    {
        auto& c = casings[i]; c.id = i + 1; c.box = 1; c.shape_pose = pose(0, 0, 0);
        c.pose = pose((int(i % 32) - 16) * .038f, .1f + (i / 1024) * .014f, (int((i / 32) % 32) - 16) * .014f);
        c.radius = .005f; c.half_height = .012f; c.mass = .007f; c.layer_bits = c.collision_mask = 1;
    }
    rptpx_casing_commands(scene, casings.data(), population, nullptr, 0, nullptr, 0);
    std::vector<RptPxCasingState> states(population + 1); RptPxCasingContact contacts[512]; uint32_t n{}, a{}, ordinary{};
    for (int i = 0; i < 360; ++i)
    {
        rptpx_step(scene, 1.f/60, nullptr, 0, &ordinary);
        rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &n, contacts, 512, &a);
    }
    std::vector<uint64_t> actors; std::vector<RptPxBodyState> initial;
    for (int corpse = 0; corpse < 30; ++corpse)
    {
        std::vector<uint64_t> shapes;
        for (int part = 0; part < 13; ++part)
        {
            uint64_t actor{}, part_shape{};
            bd = body(2, (corpse % 3 - 1) * .45f + (part % 3 - 1) * .12f,
                .09f + (corpse / 3) * .17f, (part / 3 - 2) * .25f);
            bd.mass = 6; bd.flags = 1; bd.linear_damping = .05f;
            sd = box_shape(material, .065f, .075f, .14f, 10 + corpse * 13 + part);
            sd.geometry_kind = RPTPX_GEOMETRY_CAPSULE;
            sd.dimensions = {.075f, .14f, 0};
            sd.owner_group = corpse + 1;
            rptpx_create_actor(scene, &bd, &actor); rptpx_attach_shape(scene, actor, &sd, &part_shape);
            for (auto previous : shapes) rptpx_ignore_shape_pair(scene, previous, part_shape);
            shapes.push_back(part_shape); actors.push_back(actor);
            RptPxBodyState value{}; value.struct_size = sizeof(value); value.actor = actor; value.pose = bd.pose;
            initial.push_back(value);
        }
    }
    for (auto& value : initial)
    {
        value.linear_velocity = { value.pose.position.x * 4, 6, value.pose.position.z * 4 };
        rptpx_set_actor_state(scene, value.actor, &value, 0, 1);
    }
    RptPxCasingImpact blast{}; blast.event_id = 1; blast.kind = 1;
    blast.start = {0, .02f, 0}; blast.radius = 7; blast.strength = 12;
    auto before = std::chrono::steady_clock::now();
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &blast, 1);
    std::cout << "MIXED impactMs=" << std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count() << std::endl;
    std::vector<RptPxBodyState> bodies(actors.size()); double maximum{}, total{};
    for (int i = 0; i < 180; ++i)
    {
        before = std::chrono::steady_clock::now();
        int status = rptpx_step(scene, dt, bodies.data(), uint32_t(bodies.size()), &ordinary);
        double ms = std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-before).count();
        maximum = std::max(maximum, ms); total += ms;
        rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &n, contacts, 512, &a);
        std::cout << "MIXED step=" << i << " ms=" << ms << " poses=" << n << " status=" << status << std::endl;
    }
    std::cout << "MIXED_DONE casings=" << population << " corpseBodies=" << actors.size() << " meanMs=" << total/180 << " maxMs=" << maximum << std::endl;
    rptpx_destroy_scene(scene);
}

void test_casings(uint32_t population, bool box, bool pile, bool benchmark)
{
    RptPxSceneDesc desc{};
    desc.dispatcher_threads = 2;
    desc.struct_size = sizeof(desc); desc.gravity = { 0, -9.81f, 0 };
    desc.scene_flags = 0x42; desc.graphical_casings = 1;
    desc.bounce_threshold_velocity = 0.2f;
    void* scene{};
    check(rptpx_create_scene(&desc, &scene) == 0, "graphical casing scene");
    RptPxMaterialDesc md{}; md.struct_size = sizeof(md); md.static_friction = .5f; md.dynamic_friction = .4f;
    uint64_t material{}, floor{}, shape{};
    rptpx_create_material(scene, &md, &material);
    auto bd = body(0, 0, -.01f, 0);
    auto sd = box_shape(material, 30, .01f, 30, 100);
    rptpx_create_actor(scene, &bd, &floor); rptpx_attach_shape(scene, floor, &sd, &shape);
    std::vector<RptPxCasingDesc> creates(population);
    for (uint32_t i = 0; i < population; ++i)
    {
        auto& c = creates[i]; c.id = i + 1;
        c.pose = pile ? pose((int(i % 16) - 8) * .036f, .15f + (i / 256) * .013f, (int((i / 16) % 16) - 8) * .012f) :
            pose((int(i % 128) - 64) * .12f, .15f, (int(i / 128) - 32) * .12f);
        c.shape_pose = pose(0, 0, 0); c.radius = .005f; c.half_height = .012f; c.mass = .007f;
        c.layer_bits = c.collision_mask = 1; c.box = box ? 1 : 0;
    }
    const auto create_started = std::chrono::steady_clock::now();
    check(rptpx_casing_commands(scene, creates.data(), population, nullptr, 0, nullptr, 0) == 0, "batched casing creation");
    const double create_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - create_started).count();
    std::vector<RptPxCasingState> states(population + 1), latest(population + 1);
    RptPxCasingContact audio[512]; uint32_t count{}, audio_count{}, ordinary{};
    bool valid = true; uint64_t published{}, sounds{};
    double simulation_ms{}, read_ms{}, last_second_ms{}, max_step_ms{};
    const auto started = std::chrono::steady_clock::now();
    const int steps = benchmark ? 360 : 600;
    for (int i = 0; i < steps; ++i)
    {
        auto phase = std::chrono::steady_clock::now();
        valid &= rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary) == 0 && ordinary == 0;
        double step_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - phase).count();
        simulation_ms += step_ms; max_step_ms = std::max(max_step_ms, step_ms);
        if (i >= steps - 60) last_second_ms += step_ms;
        phase = std::chrono::steady_clock::now();
        valid &= rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count) == 0;
        read_ms += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - phase).count();
        published += count; sounds += audio_count;
        if (benchmark && i % 60 == 59) std::cout << "BENCH_PROGRESS n=" << population << " box=" << box << " pile=" << pile << " step=" << (i+1) << std::endl;
        for (uint32_t j = 0; j < count; ++j) latest[states[j].id] = states[j];
    }
    check(sounds > 0, "shadow contacts produce landing audio events");
    const double elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - started).count();
    uint32_t sleeping{}; float min_y = 100.f;
    for (uint32_t i = 1; i <= population; ++i) { sleeping += (latest[i].flags & 1) != 0; min_y = std::min(min_y, latest[i].pose.position.y); }
    check(valid && min_y > -.01f, "capsules stay above thin floor and do not enter corpse snapshots");
    if (!pile) check(sleeping == population, "dispersed casings naturally sleep");
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
    if (!pile) check(count == 0, "sleeping casings publish no repeated poses");
    RptPxCasingImpact impact{}; impact.event_id = 1; impact.kind = 1; impact.start = { 0, .2f, 0 }; impact.radius = 10; impact.strength = 12;
    const auto explosion_started = std::chrono::steady_clock::now();
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &impact, 1);
    const double explosion_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - explosion_started).count();
    const auto wake_started = std::chrono::steady_clock::now();
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
    const double wake_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - wake_started).count();
    check(count > 0, "radial impact wakes retained casings");
    // Isolate the direct-hit regression from the population wake benchmark.
    std::vector<uint64_t> population_removes(population);
    for (uint32_t i = 0; i < population; ++i) population_removes[i] = i + 1;
    rptpx_casing_commands(scene, nullptr, 0, population_removes.data(), population, nullptr, 0);
    rptpx_casing_audio(scene, {0,0,0}, 0);
    // Isolated direct bullet and event deduplication.
    auto single = creates[0]; single.id = population + 1; single.pose = pose(10, 3, 0);
    rptpx_casing_commands(scene, &single, 1, nullptr, 0, nullptr, 0);
    impact = {}; impact.event_id = 2; impact.start = { 9, 3, 0 }; impact.end = { 11, 3, 0 }; impact.direction = { 1, 0, 0 }; impact.strength = 1;
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &impact, 1);
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &impact, 1);
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
    float speed{}; for (uint32_t i = 0; i < count; ++i) if (states[i].id == single.id) speed = states[i].velocity.x;
    check(speed > 19 && speed <= 20.01f, "direct bullet impulse bounded and deduplicated");
    check(audio_count == 0, "audio suppression never suppresses physical impulses");
    if (!benchmark)
    {
        RptPxCasingState last{};
        for (int tick = 0; tick < 1920; ++tick)
        {
            rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
            rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
            for (uint32_t i = 0; i < count; ++i) if (states[i].id == single.id) last = states[i];
        }
        impact.start = impact.end = last.pose.position;
        impact.start.x -= 1; impact.end.x += 1;
        rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &impact, 1);
        rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
        rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
        float after = last.velocity.x;
        for (uint32_t i = 0; i < count; ++i) if (states[i].id == single.id) after = states[i].velocity.x;
        check(after <= last.velocity.x + .01f, "event deduplication survives long custom-ammunition lifetimes");
    }
    std::vector<uint64_t> removes(population + 1); for (uint32_t i = 0; i <= population; ++i) removes[i] = i + 1;
    rptpx_casing_commands(scene, nullptr, 0, removes.data(), uint32_t(removes.size()), nullptr, 0);
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &count, audio, 512, &audio_count);
    check(count == 0, "removed casings never publish stale poses");
    std::cout << "CASING_BENCH count=" << population << " box=" << box << " pile=" << pile
        << " meanMs=" << elapsed / steps << " createMs=" << create_ms << " simulationMs=" << simulation_ms/steps
        << " readMs=" << read_ms/steps << " lastSecondMs=" << last_second_ms/60 << " maxStepMs=" << max_step_ms
        << " explosionMs=" << explosion_ms << " wakeStepMs=" << wake_ms << " sleeping=" << sleeping << " poses=" << published << " sounds=" << sounds << '\n';
    rptpx_destroy_scene(scene);
}

void test_headless_casings()
{
    RptPxSceneDesc desc{}; desc.struct_size = sizeof(desc);
    void* scene{}; rptpx_create_scene(&desc, &scene);
    uint32_t states{}, audio{};
    check(rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, nullptr, 0) == RPTPX_INVALID_ARGUMENT &&
        rptpx_casing_read(scene, nullptr, 0, &states, nullptr, 0, &audio) == RPTPX_INVALID_ARGUMENT,
        "headless scene has no casing service or event storage");
    rptpx_destroy_scene(scene);
}

void benchmark_casing_fire(uint32_t population)
{
    RptPxSceneDesc desc{}; desc.struct_size = sizeof(desc); desc.dispatcher_threads = 2;
    desc.graphical_casings = 1; desc.scene_flags = 0x42; desc.gravity = {0, -9.81f, 0};
    void* scene{}; check(rptpx_create_scene(&desc, &scene) == 0, "sustained firing scene");
    RptPxMaterialDesc md{}; md.struct_size = sizeof(md); md.static_friction = .5f; md.dynamic_friction = .4f;
    uint64_t material{}, floor{}, shape{}; rptpx_create_material(scene, &md, &material);
    auto bd = body(0, 0, -.01f, 0); auto sd = box_shape(material, 30, .01f, 30, 100);
    rptpx_create_actor(scene, &bd, &floor); rptpx_attach_shape(scene, floor, &sd, &shape);
    std::vector<RptPxCasingState> states(population + 1), latest(population + 1);
    RptPxCasingContact contacts[512]; uint32_t n{}, a{}, ordinary{};
    double create_ms{}, simulation_ms{}, read_ms{}, max_ms{}; uint64_t poses{};
    const uint32_t steps = population * 4 + 600;
    bool valid = true;
    for (uint32_t tick = 0; tick < steps; ++tick)
    {
        if (tick % 4 == 0 && tick / 4 < population)
        {
            uint32_t index = tick / 4;
            RptPxCasingDesc c{}; c.id = index + 1; c.box = 1;
            c.pose = pose((int(index % 128) - 64) * .12f, 1.f, (int(index / 128) - 32) * .12f);
            c.shape_pose = pose(0, 0, 0); c.radius = .005f; c.half_height = .012f; c.mass = .007f;
            c.velocity = { .8f, 2.f, 0 }; c.angular_velocity = { 0, 0, 20 }; c.layer_bits = c.collision_mask = 1;
            auto start = std::chrono::steady_clock::now();
            valid &= rptpx_casing_commands(scene, &c, 1, nullptr, 0, nullptr, 0) == 0;
            create_ms += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        }
        auto start = std::chrono::steady_clock::now();
        valid &= rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary) == 0;
        double elapsed = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        simulation_ms += elapsed; max_ms = std::max(max_ms, elapsed);
        start = std::chrono::steady_clock::now();
        valid &= rptpx_casing_read(scene, states.data(), uint32_t(states.size()), &n, contacts, 512, &a) == 0;
        read_ms += std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        poses += n;
        for (uint32_t i = 0; i < n; ++i) latest[states[i].id] = states[i];
    }
    uint32_t sleeping{};
    for (uint32_t i = 1; i <= population; ++i) { valid &= latest[i].id == i && latest[i].pose.position.y >= -.01f; sleeping += (latest[i].flags & 1) != 0; }
    check(valid, "sustained firing preserves all generated casing poses and floor contacts");
    std::vector<uint64_t> removes(population); for (uint32_t i = 0; i < population; ++i) removes[i] = i + 1;
    auto start = std::chrono::steady_clock::now();
    rptpx_casing_commands(scene, nullptr, 0, removes.data(), population, nullptr, 0);
    double remove_ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
    std::cout << "CASING_FIRE count=" << population << " rate=15 simulationMs=" << simulation_ms / steps
        << " readMs=" << read_ms / steps << " maxStepMs=" << max_ms << " createPerActorMs=" << create_ms / population
        << " removeAllMs=" << remove_ms << " sleeping=" << sleeping << " poses=" << poses << std::endl;
    rptpx_destroy_scene(scene);
}

void test_casing_contacts_and_occlusion()
{
    for (bool capsule_corpse : { false, true })
    for (bool box : { false, true }) for (uint32_t kind : { 1u, 2u }) for (bool corpse_moves : { false, true })
    {
        RptPxSceneDesc d{}; d.struct_size = sizeof(d); d.graphical_casings = 1; d.scene_flags = 0x42;
        void* scene{}; rptpx_create_scene(&d, &scene);
        RptPxMaterialDesc material_desc{}; material_desc.struct_size = sizeof(material_desc);
        uint64_t material{}, corpse{}, shape{};
        rptpx_create_material(scene, &material_desc, &material);
        auto corpse_desc = body(kind, corpse_moves ? -.5f : 0.f, 1, 0);
        corpse_desc.flags = 1; corpse_desc.mass = 10; corpse_desc.linear_velocity = { corpse_moves ? 5.f : 0.f, 0, 0 };
        // Kinematics use discrete contacts in PhysX 4.1; their swept-CCD flag is
        // unsupported. Keep this mover thick enough for its 5 m/s target steps.
        auto geometry = box_shape(material, kind == 2 ? .1f : .05f, .2f, .2f, 1);
        geometry.layer_bits = geometry.collides_with_bits = 1u << 23;
        // Environment roots can have negative Unity instance IDs, which reach
        // the unsigned owner group with its high bit set. They are not casings.
        geometry.owner_group = capsule_corpse ? 7u : 0xfffffff0u;
        if (capsule_corpse)
        {
            geometry.geometry_kind = RPTPX_GEOMETRY_CAPSULE;
            geometry.dimensions = { .1f, .2f, 0 };
            geometry.local_pose.rotation = axis_angle_x(1.5707963f);
        }
        check(rptpx_create_actor(scene, &corpse_desc, &corpse) == 0 && rptpx_attach_shape(scene, corpse, &geometry, &shape) == 0, "corpse contact fixture actors");
        RptPxCasingDesc casing{}; casing.id = 1; casing.pose = pose(corpse_moves ? 0.f : -.5f, 1, 0);
        casing.box = box ? 1 : 0;
        casing.shape_pose = pose(0, 0, 0); casing.radius = .005f; casing.half_height = .012f; casing.mass = .007f;
        casing.layer_bits = casing.collision_mask = 1u << 23; casing.velocity = { corpse_moves ? 0.f : 20.f, 0, 0 };
        rptpx_casing_commands(scene, &casing, 1, nullptr, 0, nullptr, 0);
        std::vector<RptPxBodyState> ordinary;
        RptPxCasingState states[4], latest{}; RptPxCasingContact sounds[512]; uint32_t n{}, a{};
        for (int i = 0; i < 20; ++i)
        {
            if (kind == 2 && corpse_moves)
            {
                auto target_pose = corpse_desc.pose;
                target_pose.position.x += corpse_desc.linear_velocity.x * (i + 1) / 60.f;
                rptpx_set_actor_pose(scene, corpse, &target_pose, 1);
            }
            step(scene, 1.f / 60, ordinary);
            rptpx_casing_read(scene, states, 4, &n, sounds, 512, &a);
            if (n) latest = states[0];
        }
        const auto target = find(ordinary, corpse);
        std::cout << "CONTACT corpseMoves=" << corpse_moves << " casingX=" << latest.pose.position.x << " casingY=" << latest.pose.position.y << " targetY=" << target.pose.position.y << " targetX=" << target.pose.position.x << " targetV=" << target.linear_velocity.x << std::endl;
        check((kind == 2 || std::abs(target.linear_velocity.x - corpse_desc.linear_velocity.x) < .00001f) &&
            std::abs(target.pose.position.x - (corpse_desc.pose.position.x + corpse_desc.linear_velocity.x / 3)) < .0001f,
            "casing contacts leave dynamic and kinematic corpse trajectories unchanged");
        check(corpse_moves ? latest.pose.position.x > .5f : latest.pose.position.x < 0.f,
            "dynamic CCD and kinematic discrete corpse contacts push or deflect casings");
        rptpx_destroy_scene(scene);
    }

    RptPxSceneDesc d{}; d.struct_size = sizeof(d); d.graphical_casings = 1; d.scene_flags = 0x42;
    void* scene{}; rptpx_create_scene(&d, &scene);
    RptPxMaterialDesc md{}; md.struct_size = sizeof(md);
    uint64_t material{}, wall{}, shape{}; rptpx_create_material(scene, &md, &material);
    auto wall_desc = body(0, 0, .5f, 0); auto wall_shape = box_shape(material, .05f, 2, 2, 1);
    rptpx_create_actor(scene, &wall_desc, &wall); rptpx_attach_shape(scene, wall, &wall_shape, &shape);
    RptPxCasingDesc cs[2]{};
    for (int i = 0; i < 2; ++i)
    {
        cs[i].id = i + 1; cs[i].box = 1; cs[i].pose = pose(i ? .6f : -.4f, .5f, 0); cs[i].shape_pose = pose(0, 0, 0);
        cs[i].radius = .005f; cs[i].half_height = .012f; cs[i].mass = .007f; cs[i].layer_bits = cs[i].collision_mask = 1;
    }
    rptpx_casing_commands(scene, cs, 2, nullptr, 0, nullptr, 0);
    RptPxCasingImpact blast{}; blast.event_id = 1; blast.kind = 1; blast.start = { -.2f, .5f, 0 }; blast.radius = 2; blast.strength = 12;
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &blast, 1);
    uint32_t n{}, a{}, ordinary{}; RptPxCasingState states[8]; RptPxCasingContact audio[512];
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states, 8, &n, audio, 512, &a);
    float visible{}, hidden{};
    for (uint32_t i = 0; i < n; ++i) { if (states[i].id == 1) visible = states[i].velocity.x; else hidden = states[i].velocity.x; }
    std::cout << "OCCLUSION visible=" << visible << " hidden=" << hidden << " states=" << n << std::endl;
    check(visible < -1 && std::abs(hidden) < .0001f, "wall occludes casing blast impulse");
    uint64_t removed[] = { 1, 2 }; rptpx_casing_commands(scene, cs, 0, removed, 2, nullptr, 0);
    rptpx_casing_commands(scene, cs, 2, nullptr, 0, nullptr, 0);
    blast.event_id = 2; blast.kind = 2; blast.radius = .35f; blast.strength = 2; blast.direction = { 0, 1, 0 };
    rptpx_casing_commands(scene, nullptr, 0, nullptr, 0, &blast, 1);
    rptpx_step(scene, 1.f / 60, nullptr, 0, &ordinary);
    rptpx_casing_read(scene, states, 8, &n, audio, 512, &a);
    visible = hidden = 0;
    for (uint32_t i = 0; i < n; ++i) { if (states[i].id == 1) visible = states[i].velocity.y; else hidden = states[i].velocity.y; }
    check(visible > .1f && visible <= 2.f && hidden == 0, "surface scatter is local, bounded and leaves distant casing untouched");
    rptpx_destroy_scene(scene);
}

void test_casing_supports()
{
    RptPxSceneDesc d{}; d.struct_size = sizeof(d); d.graphical_casings = 1;
    d.scene_flags = 0x42; d.gravity = {0, -9.81f, 0};
    void* scene{}; rptpx_create_scene(&d, &scene);
    RptPxMaterialDesc md{}; md.struct_size = sizeof(md); md.static_friction = .6f; md.dynamic_friction = .5f;
    uint64_t material{}; rptpx_create_material(scene, &md, &material);
    struct Sample { int16_t height; uint8_t m0, m1; };
    Sample samples[9]{}; samples[0].m0 = samples[0].m1 = 127;
    RptPxHeightFieldDesc hd{}; hd.struct_size = sizeof(hd); hd.rows = hd.columns = 3;
    hd.sample_stride = sizeof(Sample); hd.samples = reinterpret_cast<uint8_t*>(samples); hd.sample_bytes = sizeof(samples);
    uint64_t height{}, terrain{}, shape{}, platform{};
    check(rptpx_create_heightfield(scene, &hd, &height) == 0, "casing terrain with native hole");
    auto bd = body(0, 0, 0, 0); auto sd = box_shape(material, .01f, 2, 2, 1);
    sd.geometry_kind = RPTPX_GEOMETRY_HEIGHTFIELD; sd.mesh_handle = height;
    rptpx_create_actor(scene, &bd, &terrain); rptpx_attach_shape(scene, terrain, &sd, &shape);
    bd = body(2, 10, 0, 0); sd = box_shape(material, 1, .1f, 1, 2);
    rptpx_create_actor(scene, &bd, &platform); rptpx_attach_shape(scene, platform, &sd, &shape);
    // Three stair treads, spatially separate from terrain and the mover.
    for (int i = 0; i < 3; ++i)
    {
        uint64_t tread{}; bd = body(0, 20 + float(i), float(i) * .2f, 0);
        sd = box_shape(material, .5f, .1f, 1, 10 + i);
        rptpx_create_actor(scene, &bd, &tread); rptpx_attach_shape(scene, tread, &sd, &shape);
    }
    RptPxCasingDesc cases[6]{};
    for (int i = 0; i < 6; ++i)
    {
        auto& c = cases[i]; c.id = i + 1; c.box = 1; c.shape_pose = pose(0, 0, 0);
        c.pose = i < 2 ? pose(i ? 3.f : 1.f, 1, 1) : i == 2 ? pose(10, 1, 0) : pose(20 + float(i - 3), 1, 0);
        c.radius = .005f; c.half_height = .012f; c.mass = .007f; c.layer_bits = c.collision_mask = 1;
    }
    rptpx_casing_commands(scene, cases, 6, nullptr, 0, nullptr, 0);
    RptPxCasingState states[8], latest[7]{}; RptPxCasingContact audio[512]; uint32_t n{}, a{};
    std::vector<RptPxBodyState> ordinary;
    for (int i = 0; i < 240; ++i)
    {
        step(scene, 1.f / 60, ordinary); rptpx_casing_read(scene, states, 8, &n, audio, 512, &a);
        for (uint32_t j = 0; j < n; ++j) latest[states[j].id] = states[j];
    }
    check(latest[1].pose.position.y < -5 && latest[2].pose.position.y > 0 && latest[2].pose.position.y < .02f,
        "casing rests on terrain while native holes remain open");
    bool stairs = true;
    for (int i = 0; i < 3; ++i) stairs &= std::abs(latest[i + 4].pose.position.y - (.105f + .2f * i)) < .02f;
    check(stairs, "casings rest on separate stair treads");
    check((latest[3].flags & 1) != 0, "casing sleeps on movable support");
    for (int i = 0; i < 60; ++i)
    {
        auto target = pose(10, .005f * (i + 1), 0);
        rptpx_set_actor_pose(scene, platform, &target, 1); step(scene, 1.f / 60, ordinary);
        rptpx_casing_read(scene, states, 8, &n, audio, 512, &a);
        for (uint32_t j = 0; j < n; ++j) latest[states[j].id] = states[j];
    }
    check(latest[3].pose.position.y > .38f, "moving support physically wakes and carries casing");
    rptpx_destroy_scene(scene);
}
