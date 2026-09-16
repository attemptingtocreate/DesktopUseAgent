import importlib.util
import os
import sys
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
VALIDATION_PATH = os.path.join(ROOT, "desktopuseagent_blender", "validation.py")
spec = importlib.util.spec_from_file_location("desktopuseagent_validation", VALIDATION_PATH)
validation = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(validation)


class ValidationTests(unittest.TestCase):
    def test_rejects_relative_output(self):
        with self.assertRaises(RuntimeError):
            validation.validate_output_file("out.png")

    def test_rejects_existing_without_overwrite(self):
        path = os.path.abspath(__file__)
        with self.assertRaises(RuntimeError):
            validation.validate_output_file(path, overwrite=False)

    def test_rejects_unsupported_render_extension(self):
        with self.assertRaises(RuntimeError):
            validation.validate_output_file(
                os.path.abspath("x.bad"),
                allowed_extensions=validation.RENDER_EXTENSIONS,
            )

    def test_rejects_non_blend_save(self):
        with self.assertRaises(RuntimeError):
            validation.validate_blend_destination(os.path.abspath("x.txt"))

    def test_rejects_unsupported_mesh_format(self):
        with self.assertRaises(RuntimeError):
            validation.validate_mesh_format("dae")


class BridgeStructureTests(unittest.TestCase):
    def test_bridge_uses_background_thread_and_bounded_queues(self):
        bridge_path = os.path.join(ROOT, "desktopuseagent_blender", "bridge.py")
        with open(bridge_path, encoding="utf-8") as handle:
            source = handle.read()
        self.assertIn("threading.Thread", source)
        self.assertIn("queue.Queue(maxsize=", source)
        self.assertIn("_network_worker", source)
        self.assertIn("_drain_main_thread", source)
        self.assertNotIn("_poll_tick", source)
        # Network I/O must not run inside the main-thread timer drain.
        drain_start = source.index("def _drain_main_thread")
        next_def = source.find("\ndef ", drain_start + 1)
        drain_body = source[drain_start:next_def] if next_def != -1 else source[drain_start:]
        self.assertNotIn("urlopen", drain_body)
        self.assertNotIn("_post(", drain_body)
        self.assertNotIn("_get(", drain_body)


if __name__ == "__main__":
    unittest.main()
