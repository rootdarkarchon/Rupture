// Validate the diagnostic against authored vertices, not against its own output.
void check_collider_wireframe(void* scene, uint64_t shape, const std::vector<RptPxVec3>& expected, uint32_t count)
{
    uint32_t required{};
    check(rptpx_get_shape_wireframe(scene, shape, nullptr, 0, &required) == RPTPX_BUFFER_TOO_SMALL && required == count,
        "collider wireframe reports exact edge endpoint count");
    const RptPxVec3 sentinel{ 12345.f, 12345.f, 12345.f };
    std::vector<RptPxVec3> output(required + 2, sentinel);
    check(rptpx_get_shape_wireframe(scene, shape, output.data() + 1, required - 1, &required) == RPTPX_BUFFER_TOO_SMALL &&
        output[1].x == sentinel.x, "short wireframe buffer is rejected without partial writes");
    check(rptpx_get_shape_wireframe(scene, shape, output.data() + 1, count, &required) == RPTPX_OK &&
        output.front().x == sentinel.x && output.back().x == sentinel.x,
        "collider wireframe preserves output buffer guards");
    auto index_of = [&](const RptPxVec3& point) {
        for (size_t i = 0; i < expected.size(); ++i)
            if (std::abs(point.x - expected[i].x) < 1.e-4f && std::abs(point.y - expected[i].y) < 1.e-4f &&
                std::abs(point.z - expected[i].z) < 1.e-4f) return static_cast<int>(i);
        return -1;
    };
    bool valid = true;
    std::vector<uint32_t> seen;
    for (uint32_t i = 1; i + 1 <= count; i += 2)
    {
        int a = index_of(output[i]), b = index_of(output[i + 1]);
        if (a < 0 || b < 0 || a == b) { valid = false; continue; }
        uint32_t key = (uint32_t(std::min(a, b)) << 16) | uint32_t(std::max(a, b));
        if (std::find(seen.begin(), seen.end(), key) != seen.end()) valid = false;
        seen.push_back(key);
    }
    check(valid, "wireframe contains unique real polygon edges with shape pose and scale applied once");
}

void test_box_wireframe(void* scene, uint64_t material)
{
    auto actor_desc = body(RPTPX_ACTOR_STATIC, 1000.f, 2000.f, 3000.f);
    uint64_t actor{}, shape{};
    auto geometry = box_shape(material, 1.f, 2.f, 3.f, 999);
    geometry.local_pose = pose(4.f, 5.f, 6.f);
    geometry.local_pose.rotation = axis_angle_x(3.14159265359f * .5f);
    check(rptpx_create_actor(scene, &actor_desc, &actor) == RPTPX_OK &&
        rptpx_attach_shape(scene, actor, &geometry, &shape) == RPTPX_OK, "offset rotated debug box setup");
    std::vector<RptPxVec3> expected;
    for (int x : { -1, 1 }) for (int y : { -1, 1 }) for (int z : { -1, 1 })
        expected.push_back({ 4.f + x, 5.f - 3.f * z, 6.f + 2.f * y });
    check_collider_wireframe(scene, shape, expected, 24);
    check(rptpx_get_shape_wireframe(scene, shape, nullptr, 1, nullptr) == RPTPX_INVALID_ARGUMENT,
        "wireframe rejects invalid buffers");
    check(rptpx_destroy_actor(scene, actor) == RPTPX_OK, "debug box cleanup");
    uint32_t required{};
    check(rptpx_get_shape_wireframe(scene, shape, nullptr, 0, &required) == RPTPX_INVALID_HANDLE && required == 0,
        "wireframe rejects stale shapes after weapon release");
}
