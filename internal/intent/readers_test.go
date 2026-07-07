package intent

import "testing"

func TestAllReaders_NoDisabled(t *testing.T) {
	got := AllReaders(nil)
	if len(got) != 4 {
		t.Errorf("expected 4 readers, got %d", len(got))
	}
}

func TestAllReaders_Disabled(t *testing.T) {
	got := AllReaders(map[string]bool{"codex": true, "pi": true})
	if len(got) != 2 {
		t.Errorf("expected 2 readers, got %d", len(got))
	}
	for _, r := range got {
		if r.Name() == "codex" || r.Name() == "pi" {
			t.Errorf("disabled reader %q present", r.Name())
		}
	}
}

func TestAllReaders_RemovedToolsExcluded(t *testing.T) {
	for _, r := range AllReaders(nil) {
		if r.Name() == "opencode" || r.Name() == "rovodev" {
			t.Fatalf("removed reader %q should not be registered", r.Name())
		}
	}
}
