// Explicit fixture for the earlier app checkout boundary. boundary-inventory.mjs verifies this
// command list against that checkout's src-tauri/src/lib.rs whenever that real producer is present.
tauri::generate_handler![
    append_renderer_log,
    current_principal,
    get_data_location_status,
    get_device_capability_profile,
    get_peer_sync_config,
    capability_cp_demo_execute,
    capability_health,
    capability_invoke,
    node_status,
    set_peer_sync_config,
]
