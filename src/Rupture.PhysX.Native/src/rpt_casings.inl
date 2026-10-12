// Included inside the bridge's private namespace. Allocated only for graphical scenes.
constexpr PxU32 casing_marker = 0x80000000u;

PxFilterFlags casing_filter_shader(PxFilterObjectAttributes a0, PxFilterData f0,
    PxFilterObjectAttributes a1, PxFilterData f1, PxPairFlags& flags, const void* data, PxU32 size)
{
    const auto result = filter_shader(a0, f0, a1, f1, flags, data, size);
    // Ordinary shapes have a unique nonzero pair-filter ID. Unity environment
    // instance IDs are signed and can also set word2's high bit.
    const bool casing0 = (f0.word2 & casing_marker) && f0.word3 == 0;
    const bool casing1 = (f1.word2 & casing_marker) && f1.word3 == 0;
    if (!(result & PxFilterFlag::eSUPPRESS) && (casing0 || casing1))
        flags |= PxPairFlag::eNOTIFY_TOUCH_FOUND | PxPairFlag::eNOTIFY_CONTACT_POINTS |
            PxPairFlag::eNOTIFY_TOUCH_CCD | PxPairFlag::ePRE_SOLVER_VELOCITY;
    // Ordinary pile contacts avoid PhysX's quadratic CCD island sorting.
    // Swept contacts remain enabled against environment and corpse shapes.
    if (casing0 && casing1)
    {
        flags.clear(PxPairFlag::eDETECT_CCD_CONTACT);
        flags.clear(PxPairFlag::eNOTIFY_TOUCH_CCD);
    }
    return (casing0 || casing1) && !(result & PxFilterFlag::eSUPPRESS) ? PxFilterFlags(PxFilterFlag::eDEFAULT) : result;
}

class CasingWorld final : public PxSimulationEventCallback
{
    struct Entry
    {
        uint64_t id{};
        bool dirty{}, retiring{};
        PxRigidDynamic* actor{};
        std::unordered_set<uint64_t> events;
        ~Entry() { if (actor) actor->release(); }
    };
    PxPhysics& physics;
    PxScene& scene;
    PxMaterial* material{};
    std::unordered_map<uint64_t, std::unique_ptr<Entry>> entries;
    std::vector<Entry*> changed;
    std::vector<PxActor*> retiring_actors;
    std::vector<RptPxCasingState> failed;
    std::vector<RptPxCasingContact> contacts;
    std::vector<std::pair<Entry*, PxVec3>> query_hits;
    struct Impulse { Entry* entry; PxVec3 force, point; };
    std::vector<Impulse> impulses;
    std::mutex callback_gate;
    PxVec3 listener{0.f};
    bool audible = true;

    void mark(Entry* e) { if (!e->dirty) { e->dirty = true; changed.push_back(e); } }

    static Entry* entry(const PxActor* actor)
    {
        return actor && actor->getDominanceGroup() == 1 ? static_cast<Entry*>(actor->userData) : nullptr;
    }
    struct QueryFilter final : PxQueryFilterCallback
    {
        bool casings;
        explicit QueryFilter(bool value) : casings(value) {}
        PxQueryHitType::Enum preFilter(const PxFilterData&, const PxShape*, const PxRigidActor* actor, PxHitFlags&) override
        {
            return (entry(actor) != nullptr) == casings ?
                (casings ? PxQueryHitType::eTOUCH : PxQueryHitType::eBLOCK) : PxQueryHitType::eNONE;
        }
        PxQueryHitType::Enum postFilter(const PxFilterData&, const PxQueryHit&) override { return PxQueryHitType::eNONE; }
    };
    bool exposed(const PxVec3& origin, const PxVec3& target)
    {
        PxVec3 delta = target - origin;
        const float length = delta.normalize();
        if (length < 0.002f) return true;
        QueryFilter filter(false);
        PxRaycastBuffer hit;
        return !scene.raycast(origin, delta, std::max(0.001f, length - 0.002f), hit, PxHitFlag::eDEFAULT,
            PxQueryFilterData(PxQueryFlag::eSTATIC | PxQueryFlag::eDYNAMIC | PxQueryFlag::ePREFILTER), &filter);
    }
    void impulse(Entry& value, const RptPxCasingImpact& input, const PxVec3& point)
    {
        if (value.events.count(input.event_id)) return;
        PxVec3 direction = to_px(input.direction);
        float speed = input.strength;
        if (input.kind != 0)
        {
            const PxVec3 center = value.actor->getGlobalPose().transform(value.actor->getCMassLocalPose().p);
            direction = center - to_px(input.start);
            const float distance = direction.normalize();
            if (distance >= input.radius || !exposed(to_px(input.start), center)) return;
            speed *= std::max(0.f, 1.f - distance / input.radius);
            if (input.kind == 2) direction = (direction + to_px(input.direction)).getNormalized();
            else if (distance < 0.00001f) direction = PxVec3(0, 1, 0);
        }
        else
        {
            if (direction.normalize() < 0.00001f) return;
            speed = std::min(20.f, input.strength / value.actor->getMass());
        }
        if (!(speed > 0.f)) return;
        // Receipt lifetime matches this retained generation, including custom
        // ammunition with a flight lifetime longer than the stock 13 seconds.
        value.events.insert(input.event_id);
        impulses.push_back({ &value, direction * (value.actor->getMass() * speed), point });
    }
    struct RayHits final : PxRaycastCallback
    {
        PxRaycastHit buffer[64];
        CasingWorld& world;
        const RptPxCasingImpact& impact;
        RayHits(CasingWorld& w, const RptPxCasingImpact& i) : PxRaycastCallback(buffer, 64), world(w), impact(i) {}
        PxAgain processTouches(const PxRaycastHit* hits, PxU32 count) override
        {
            for (PxU32 i = 0; i < count; ++i) if (auto* e = entry(hits[i].actor)) world.query_hits.emplace_back(e, hits[i].position);
            return true;
        }
    };
    struct OverlapHits final : PxOverlapCallback
    {
        PxOverlapHit buffer[128];
        CasingWorld& world;
        const RptPxCasingImpact& impact;
        OverlapHits(CasingWorld& w, const RptPxCasingImpact& i) : PxOverlapCallback(buffer, 128), world(w), impact(i) {}
        PxAgain processTouches(const PxOverlapHit* hits, PxU32 count) override
        {
            for (PxU32 i = 0; i < count; ++i) if (auto* e = entry(hits[i].actor))
                world.query_hits.emplace_back(e, e->actor->getGlobalPose().p);
            return true;
        }
    };
public:
    void audio(PxVec3 position, bool enabled) { listener = position; audible = enabled; }
    CasingWorld(PxPhysics& p, PxScene& s) : physics(p), scene(s)
    {
        material = physics.createMaterial(0.6f, 0.5f, 0.1f);
        contacts.reserve(512);
        scene.setDominanceGroupPair(0, 1, PxDominanceGroupPair(0, 1));
        scene.setSimulationEventCallback(this);
    }
    ~CasingWorld()
    {
        scene.setSimulationEventCallback(nullptr);
        retiring_actors.clear();
        for (const auto& value : entries) retiring_actors.push_back(value.second->actor);
        if (!retiring_actors.empty()) scene.removeActors(retiring_actors.data(), static_cast<PxU32>(retiring_actors.size()), false);
        entries.clear();
        if (material) material->release();
    }
    void create(const RptPxCasingDesc& d)
    {
        if (entries.count(d.id)) return;
        auto e = std::make_unique<Entry>();
        e->id = d.id;
        if (!material || !d.id || !finite(d.pose.position) || !finite(d.pose.rotation) ||
            !finite(d.shape_pose.position) || !finite(d.shape_pose.rotation) || !finite(d.velocity) ||
            !finite(d.angular_velocity) || !finite(d.radius) || !finite(d.half_height) ||
            !finite(d.mass) || d.radius < 0.001f || d.radius > 0.1f || d.half_height < 0.f ||
            d.half_height > 0.25f || d.mass <= 0.f || d.mass > 0.25f)
        { failed.push_back({ d.id, d.pose, {}, 2 }); return; }
        e->actor = physics.createRigidDynamic(to_px(d.pose));
        if (!e->actor) { failed.push_back({ d.id, d.pose, {}, 2 }); return; }
        PxShape* shape = d.box ? physics.createShape(PxBoxGeometry(d.half_height + d.radius, d.radius, d.radius), *material, true) :
            physics.createShape(PxCapsuleGeometry(d.radius, d.half_height), *material, true);
        if (!shape) { failed.push_back({ d.id, d.pose, {}, 2 }); return; }
        shape->setLocalPose(to_px(d.shape_pose));
        shape->setContactOffset(std::min(0.001f, d.radius * 0.2f));
        shape->setRestOffset(0.f);
        const PxFilterData filter(d.layer_bits, d.collision_mask, casing_marker, 0);
        shape->setSimulationFilterData(filter);
        shape->setQueryFilterData(filter);
        e->actor->attachShape(*shape);
        shape->release();
        PxRigidBodyExt::setMassAndUpdateInertia(*e->actor, d.mass);
        e->actor->setDominanceGroup(1);
        e->actor->userData = e.get();
        e->actor->setActorFlag(PxActorFlag::eSEND_SLEEP_NOTIFIES, true);
        e->actor->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_CCD, true);
        e->actor->setLinearDamping(0.05f);
        e->actor->setAngularDamping(1.5f);
        e->actor->setSleepThreshold(0.02f);
        e->actor->setMaxAngularVelocity(150.f);
        e->actor->setMaxLinearVelocity(30.f);
        e->actor->setMaxDepenetrationVelocity(0.5f);
        e->actor->setSolverIterationCounts(12, 4);
        e->actor->setLinearVelocity(to_px(d.velocity));
        e->actor->setAngularVelocity(to_px(d.angular_velocity));
        scene.addActor(*e->actor);
        mark(e.get());
        entries.emplace(d.id, std::move(e));
    }
    void remove_batch(const uint64_t* ids, uint32_t count)
    {
        if (!count) return;
        retiring_actors.clear();
        for (uint32_t i = 0; i < count; ++i)
        {
            const auto found = entries.find(ids[i]);
            if (found != entries.end() && !found->second->retiring)
            {
                found->second->retiring = true;
                retiring_actors.push_back(found->second->actor);
            }
        }
        if (!retiring_actors.empty())
            scene.removeActors(retiring_actors.data(), static_cast<PxU32>(retiring_actors.size()), retiring_actors.size() != entries.size());
        // Compact changed publication once, including a full capacity reduction.
        // Repeated per-actor compaction made bulk retirement quadratic.
        changed.erase(std::remove_if(changed.begin(), changed.end(), [](Entry* e) { return e->retiring; }), changed.end());
        for (uint32_t i = 0; i < count; ++i) entries.erase(ids[i]);
    }
    void apply(const RptPxCasingImpact& input)
    {
        if (!input.event_id || !finite(input.start) || !finite(input.end) || !finite(input.direction) ||
            !finite(input.strength) || input.strength <= 0.f) return;
        QueryFilter filter(true);
        query_hits.clear();
        impulses.clear();
        const PxQueryFilterData query(PxQueryFlag::eDYNAMIC | PxQueryFlag::ePREFILTER | PxQueryFlag::eNO_BLOCK);
        if (input.kind == 0)
        {
            PxVec3 direction = to_px(input.end) - to_px(input.start);
            const float length = direction.normalize();
            if (length < 0.00001f) return;
            RayHits hits(*this, input);
            scene.raycast(to_px(input.start), direction, length, hits, PxHitFlag::eDEFAULT, query, &filter);
            hits.processTouches(hits.touches, hits.nbTouches);
            for (const auto& hit : query_hits) impulse(*hit.first, input, hit.second);
        }
        else if (input.kind <= 2 && finite(input.radius) && input.radius > 0.f && input.radius <= 200.f)
        {
            RptPxCasingImpact bounded = input;
            bounded.strength = std::min(input.kind == 2 ? 2.f : 12.f, input.strength);
            OverlapHits hits(*this, bounded);
            scene.overlap(PxSphereGeometry(input.radius), PxTransform(to_px(input.start)), hits, query, &filter);
            hits.processTouches(hits.touches, hits.nbTouches);
            for (const auto& hit : query_hits) impulse(*hit.first, bounded, hit.second);
        }
        // Finish all visibility queries before waking actors. Interleaving queries
        // and writes forces repeated scene-query commits during large explosions.
        for (const auto& command : impulses)
            PxRigidBodyExt::addForceAtPos(*command.entry->actor, command.force, command.point, PxForceMode::eIMPULSE);
    }
    void capture()
    {
        PxU32 count{};
        PxActor** active = scene.getActiveActors(count);
        for (PxU32 i = 0; i < count; ++i) if (auto* e = entry(active[i])) mark(e);
    }
    int32_t read(RptPxCasingState* states, uint32_t capacity, uint32_t& count,
        RptPxCasingContact* audio, uint32_t audio_capacity, uint32_t& audio_count)
    {
        count = static_cast<uint32_t>(changed.size() + failed.size());
        audio_count = static_cast<uint32_t>(contacts.size());
        if (capacity < count || audio_capacity < audio_count || (count && !states) || (audio_count && !audio)) return RPTPX_BUFFER_TOO_SMALL;
        uint32_t i{};
        for (auto* e : changed)
        {
            states[i++] = { e->id, from_px(e->actor->getGlobalPose()),
                from_px(e->actor->getLinearVelocity()), e->actor->isSleeping() ? 1u : 0u };
            e->dirty = false;
        }
        for (const auto& f : failed) states[i++] = f;
        std::copy(contacts.begin(), contacts.end(), audio);
        changed.clear(); failed.clear(); contacts.clear();
        return RPTPX_OK;
    }
    void onWake(PxActor** actors, PxU32 count) override { for (PxU32 i = 0; i < count; ++i) if (auto* e = entry(actors[i])) mark(e); }
    void onSleep(PxActor** actors, PxU32 count) override { onWake(actors, count); }
    void onConstraintBreak(PxConstraintInfo*, PxU32) override {}
    void onTrigger(PxTriggerPair*, PxU32) override {}
    void onAdvance(const PxRigidBody* const*, const PxTransform*, const PxU32) override {}
    void onContact(const PxContactPairHeader& header, const PxContactPair* pairs, PxU32 count) override
    {
        if (!audible) return;
        if (header.flags & (PxContactPairHeaderFlag::eREMOVED_ACTOR_0 | PxContactPairHeaderFlag::eREMOVED_ACTOR_1)) return;
        Entry* e = entry(header.actors[0]);
        const int side = e ? 0 : 1;
        if (!e) e = entry(header.actors[1]);
        if (!e) return;
        if ((e->actor->getGlobalPose().p - listener).magnitudeSquared() > 900.f) return;
        std::lock_guard<std::mutex> lock(callback_gate);
        if (contacts.size() >= 512) return;
        PxContactPairExtraDataIterator extra(header.extraDataStream, header.extraDataStreamSize);
        float speed = 0.f;
        while (extra.nextItemSet()) if (extra.preSolverVelocity)
            speed = (extra.preSolverVelocity->linearVelocity[0] - extra.preSolverVelocity->linearVelocity[1]).magnitude();
        for (PxU32 i = 0; i < count; ++i)
        {
            PxContactPairPoint point;
            if (pairs[i].extractContacts(&point, 1))
            {
                speed = std::max(speed, point.impulse.magnitude() / e->actor->getMass());
                if (speed < 0.2f) continue;
                contacts.push_back({ e->id, from_px(point.position), from_px(side ? -point.normal : point.normal), speed });
                break;
            }
        }
    }
};
