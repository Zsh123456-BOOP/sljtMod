extends SceneTree

func _init() -> void:
	var args: PackedStringArray = OS.get_cmdline_user_args()
	if args.size() < 2:
		push_error("Usage: --script pack_pck.gd -- <source_root> <out_pck>")
		quit(1)
		return

	var source_root: String = args[0]
	var out_pck: String = args[1]
	var manifest_path: String = source_root.path_join("mod_manifest.json")

	if not FileAccess.file_exists(manifest_path):
		push_error("mod_manifest.json not found: " + manifest_path)
		quit(2)
		return

	var packer := PCKPacker.new()
	var err: int = packer.pck_start(out_pck)
	if err != OK:
		push_error("pck_start failed: " + str(err))
		quit(3)
		return

	err = packer.add_file("res://mod_manifest.json", manifest_path)
	if err != OK:
		push_error("add_file failed: " + str(err))
		quit(4)
		return

	err = packer.flush()
	if err != OK:
		push_error("flush failed: " + str(err))
		quit(5)
		return

	print("Packed PCK: " + out_pck)
	quit(0)
