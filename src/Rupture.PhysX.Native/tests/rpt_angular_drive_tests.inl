#include <limits>

RptPxQuat angular_mul(RptPxQuat a, RptPxQuat b)
{
    return { a.w*b.x+a.x*b.w+a.y*b.z-a.z*b.y, a.w*b.y-a.x*b.z+a.y*b.w+a.z*b.x,
        a.w*b.z+a.x*b.y-a.y*b.x+a.z*b.w, a.w*b.w-a.x*b.x-a.y*b.y-a.z*b.z };
}
RptPxQuat angular_inverse(RptPxQuat q) { return {-q.x,-q.y,-q.z,q.w}; }

void test_angular_drives()
{
    for (bool locked : {false, true}) for (float dt : {1.f/30.f, 1.f/120.f})
    {
        void* scene{};
        RptPxSceneDesc desc{}; desc.struct_size=sizeof(desc); desc.bounce_threshold_velocity=2.f;
        check(rptpx_create_scene(&desc,&scene)==RPTPX_OK,"angular isolated scene");
        auto a=body(RPTPX_ACTOR_DYNAMIC,0,0,0), b=a;
        a.pose.rotation=axis_angle_x(.4f); a.sleep_threshold=b.sleep_threshold=0.f;
        uint64_t child{},parent{},joint{};
        check(rptpx_create_actor(scene,&a,&child)==RPTPX_OK && rptpx_create_actor(scene,&b,&parent)==RPTPX_OK,"angular connected dynamic actors");
        RptPxD6JointDesc j{}; j.struct_size=sizeof(j); j.actor0=child;j.actor1=parent;
        j.local_pose0=pose(0,0,0);j.local_pose1=pose(0,0,0);j.local_pose1.rotation=a.pose.rotation;
        j.twist_lower=-1.f;j.twist_upper=1.f;j.swing_y=locked?0.f:1.f;j.swing_z=1.f;
        j.mass_scale=j.connected_mass_scale=1.f;
        check(rptpx_create_d6_joint(scene,&j,&joint)==RPTPX_OK,"angular D6 connection including locked swing case");
        RptPxAngularDriveState s{}; s.struct_size=sizeof(s);s.joint=joint;
        check(rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && !s.active && s.torque_per_row==0,"new/default D6 joint has no residual drives");
        RptPxAngularDriveCommand c{};c.struct_size=sizeof(c);c.joint=joint;c.parent_to_child=a.pose.rotation;
        c.stiffness=35.f;c.damping=3.f;c.torque_per_row=2.f;c.duration=.45f;
        check(rptpx_set_d6_drives(scene,&c,1)==RPTPX_OK,"angular drive arming");
        std::vector<RptPxBodyState> states;
        check(step(scene,dt,states) && std::abs(find(states,child).angular_velocity.x)<.0001f && std::abs(find(states,parent).angular_velocity.x)<.0001f,
            "captured nonidentity body-relative pose produces no initial corrective kick");
        auto moved=find(states,child);moved.pose.rotation=axis_angle_x(.6f);
        check(rptpx_set_actor_state(scene,child,&moved,0,1)==RPTPX_OK,"angular perturbation");
        RptPxForceCommand push{};push.struct_size=sizeof(push);push.operation=RPTPX_ADD_FORCE;push.actor=child;push.force={4.f,0.f,0.f};push.mode=RPTPX_IMPULSE;push.wake=1;
        check(rptpx_apply_forces(scene,&push,1)==RPTPX_OK && step(scene,dt,states),"angular driven step preserves queued linear impulse");
        auto ac=find(states,child),ap=find(states,parent);
        check(ac.angular_velocity.x<0 && ap.angular_velocity.x>0 && std::abs(ac.angular_velocity.x)<=2.f*dt+.0001f &&
            std::abs(ac.angular_velocity.x+ap.angular_velocity.x)<.0001f,
            "SLERP/locked-axis fallback has restorative equal/opposite bounded angular response in force units");
        check(std::abs(ac.linear_velocity.x+ap.linear_velocity.x-4.f)<.001f,"angular drive adds no net linear momentum");
        check(rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && s.active && s.torque_per_row<2.f && s.stiffness<35.f && s.remaining<.45f,
            "native angular strength and force cap decay with simulated time");
        RptPxAngularDriveCommand bad[2]={c,c};bad[0].duration=0;bad[1].joint=~uint64_t(0);
        check(rptpx_set_d6_drives(scene,bad,2)==RPTPX_INVALID_ARGUMENT && rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && s.active,
            "invalid angular batch is atomic and cannot partially clear a valid drive");
        auto invalid=c;invalid.parent_to_child={0,0,0,0};
        check(rptpx_set_d6_drives(scene,&invalid,1)==RPTPX_INVALID_ARGUMENT,"angular zero quaternion rejected");
        invalid=c;invalid.duration=1.1f;
        check(rptpx_set_d6_drives(scene,&invalid,1)==RPTPX_INVALID_ARGUMENT,"angular duration bounded");
        invalid=c;invalid.torque_per_row=std::numeric_limits<float>::quiet_NaN();
        check(rptpx_set_d6_drives(scene,&invalid,1)==RPTPX_INVALID_ARGUMENT,"angular nonfinite strength rejected");
        while(s.active) { check(step(scene,dt,states),"angular fade step");rptpx_get_d6_drives(scene,&s,1); }
        const float relative=signed_x_angle(find(states,child).pose.rotation)-signed_x_angle(find(states,parent).pose.rotation);
        check(std::abs(relative-.4f)<.2f && s.stiffness==0 && s.damping==0 && s.torque_per_row==0,"residual resistance reduces pose error then reaches exactly zero");
        check(rptpx_set_d6_drives(scene,&c,1)==RPTPX_OK,"angular rearm for lifecycle check");
        moved=find(states,child);
        check(rptpx_set_actor_state(scene,child,&moved,1,0)==RPTPX_OK && rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && !s.active,
            "kinematic ownership/settlement clears residual drives before any later wake");
        rptpx_set_actor_state(scene,child,&moved,0,1);
        check(step(scene,dt,states) && rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && !s.active,"reactivation never restarts an expired/cleared drive");
        check(rptpx_set_d6_drives(scene,&c,1)==RPTPX_OK,"angular rearm before severed actor removal");
        check(rptpx_destroy_actor(scene,parent)==RPTPX_OK && rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && !s.active &&
            rptpx_set_d6_drives(scene,&c,1)==RPTPX_INVALID_ARGUMENT,"removed parent clears drive and cannot be reconnected by residual resistance");
        rptpx_destroy_scene(scene);
    }
    // Noncommuting body and joint-frame orientations exercise the actual target conversion.
    void* scene{};RptPxSceneDesc desc{};desc.struct_size=sizeof(desc);desc.bounce_threshold_velocity=2;
    rptpx_create_scene(&desc,&scene);
    auto a=body(RPTPX_ACTOR_DYNAMIC,0,0,0),b=a;
    a.pose.rotation={0,0,std::sin(.4f),std::cos(.4f)};b.pose.rotation={0,std::sin(-.35f),0,std::cos(-.35f)};
    uint64_t child{},parent{},joint{};rptpx_create_actor(scene,&a,&child);rptpx_create_actor(scene,&b,&parent);
    RptPxD6JointDesc j{};j.struct_size=sizeof(j);j.actor0=child;j.actor1=parent;j.local_pose0=pose(0,0,0);j.local_pose1=pose(0,0,0);
    j.local_pose0.rotation=axis_angle_x(.3f);const auto relative=angular_mul(angular_inverse(b.pose.rotation),a.pose.rotation);
    j.local_pose1.rotation=angular_mul(relative,j.local_pose0.rotation);j.twist_lower=-1;j.twist_upper=1;j.swing_y=j.swing_z=1;j.mass_scale=j.connected_mass_scale=1;
    rptpx_create_d6_joint(scene,&j,&joint);
    RptPxAngularDriveCommand c{};c.struct_size=sizeof(c);c.joint=joint;c.parent_to_child=relative;c.stiffness=35;c.damping=3;c.torque_per_row=8;c.duration=.45f;
    std::vector<RptPxBodyState> states;
    check(rptpx_set_d6_drives(scene,&c,1)==RPTPX_OK && step(scene,1.f/60,states) &&
        std::abs(find(states,child).angular_velocity.x)+std::abs(find(states,child).angular_velocity.y)+std::abs(find(states,child).angular_velocity.z)<.0001f,
        "angular target conversion preserves arbitrary captured body and joint-frame orientations");
    auto ac=find(states,child),ap=find(states,parent);
    rptpx_set_actor_state(scene,child,&ac,0,0);rptpx_set_actor_state(scene,parent,&ap,0,0);
    check(step(scene,1.f/60,states) && find(states,child).sleeping && find(states,parent).sleeping,
        "residual parameter updates never wake an already sleeping island");
    RptPxAngularDriveState s{};s.struct_size=sizeof(s);s.joint=joint;
    check(rptpx_get_d6_drives(scene,&s,1)==RPTPX_OK && !s.active,"sleeping island retires residual resistance");
    rptpx_destroy_scene(scene);

    // Capacity is global and transient; a full batch is validated before mutation.
    rptpx_create_scene(&desc,&scene);a=body(RPTPX_ACTOR_DYNAMIC,0,0,0);b=a;
    rptpx_create_actor(scene,&a,&child);rptpx_create_actor(scene,&b,&parent);
    j={};j.struct_size=sizeof(j);j.actor0=child;j.actor1=parent;j.local_pose0=j.local_pose1=pose(0,0,0);
    j.twist_lower=-1;j.twist_upper=1;j.swing_y=j.swing_z=1;j.mass_scale=j.connected_mass_scale=1;
    std::vector<uint64_t> handles(257);std::vector<RptPxAngularDriveCommand> batch(32);
    bool capacity_ok=true;
    for(size_t i=0;i<handles.size();i++)capacity_ok &= rptpx_create_d6_joint(scene,&j,&handles[i])==RPTPX_OK;
    for(size_t start=0;start<256;start+=32)
    {
        for(size_t i=0;i<32;i++){batch[i]=c;batch[i].joint=handles[start+i];batch[i].parent_to_child={0,0,0,1};}
        capacity_ok &= rptpx_set_d6_drives(scene,batch.data(),32)==RPTPX_OK;
    }
    c.joint=handles[256];c.parent_to_child={0,0,0,1};
    check(capacity_ok && rptpx_set_d6_drives(scene,&c,1)==RPTPX_BUSY,"native angular capacity is bounded at 256 transient joints");
    auto cancel=c;cancel.joint=handles[0];cancel.duration=0;
    check(rptpx_set_d6_drives(scene,&cancel,1)==RPTPX_OK && rptpx_set_d6_drives(scene,&c,1)==RPTPX_OK,
        "clearing a drive immediately releases transient capacity");
    rptpx_destroy_scene(scene);

    // Small matched solver fixture, not raid frametime or visual acceptance.
    double cost[2]{};
    for(int driven=0;driven<2;driven++)for(int repeat=0;repeat<3;repeat++)
    {
        rptpx_create_scene(&desc,&scene);
        std::vector<RptPxAngularDriveCommand> commands;
        for(int pair=0;pair<16;pair++)
        {
            a=body(RPTPX_ACTOR_DYNAMIC,float(pair),0,0);b=a;a.sleep_threshold=b.sleep_threshold=0;
            a.angular_velocity={3,1,2};rptpx_create_actor(scene,&a,&child);rptpx_create_actor(scene,&b,&parent);
            j.actor0=child;j.actor1=parent;rptpx_create_d6_joint(scene,&j,&joint);
            c.joint=joint;commands.push_back(c);
        }
        if(driven)rptpx_set_d6_drives(scene,commands.data(),uint32_t(commands.size()));
        const auto started=std::chrono::steady_clock::now();
        for(int frame=0;frame<24;frame++)step(scene,1.f/60.f,states);
        cost[driven]+=std::chrono::duration<double,std::milli>(std::chrono::steady_clock::now()-started).count()/72.;
        rptpx_destroy_scene(scene);
    }
    std::cout<<"ANGULAR_BENCH pairs=16 bodies=32 stepsPerRun=24 repeats=3 baselineMeanMs="<<cost[0]<<" residualMeanMs="<<cost[1]<<" (isolated fixture, no raid/environment cost)\n";
}
