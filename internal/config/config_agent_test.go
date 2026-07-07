package config

import (
	"context"
	"errors"
	"io/fs"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"

	"github.com/kunchenguid/no-mistakes/internal/types"
)

func TestAgentPath_Override(t *testing.T) {
	cfg := &Config{
		Agent:             types.AgentClaude,
		AgentPathOverride: map[string]string{"claude": "/custom/claude"},
	}
	if got := cfg.AgentPath(); got != "/custom/claude" {
		t.Errorf("AgentPath() = %q, want %q", got, "/custom/claude")
	}
}

func TestAgentPath_DefaultBinaries(t *testing.T) {
	tests := []struct {
		agent types.AgentName
		want  string
	}{
		{types.AgentClaude, "claude"},
		{types.AgentCodex, "codex"},
		{types.AgentPi, "pi"},
		{types.AgentCopilot, "copilot"},
		{types.AgentDroid, "droid"},
	}
	for _, tt := range tests {
		cfg := &Config{Agent: tt.agent}
		if got := cfg.AgentPath(); got != tt.want {
			t.Errorf("AgentPath() for %q = %q, want %q", tt.agent, got, tt.want)
		}
	}
}

func TestAgentPath_ACPUsesAcpxPath(t *testing.T) {
	tests := []struct {
		name string
		cfg  *Config
		want string
	}{
		{name: "default", cfg: &Config{Agent: "acp:gemini"}, want: "acpx"},
		{name: "override", cfg: &Config{Agent: "acp:gemini", ACPXPath: "/opt/bin/acpx"}, want: "/opt/bin/acpx"},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			if got := tt.cfg.AgentPath(); got != tt.want {
				t.Errorf("AgentPath() = %q, want %q", got, tt.want)
			}
		})
	}
}

func TestParseLogLevel(t *testing.T) {
	tests := []struct {
		input string
		want  slog.Level
	}{
		{"debug", slog.LevelDebug},
		{"info", slog.LevelInfo},
		{"warn", slog.LevelWarn},
		{"error", slog.LevelError},
		{"", slog.LevelInfo},
		{"unknown", slog.LevelInfo},
		{"DEBUG", slog.LevelInfo}, // case-sensitive, unrecognized defaults to info
	}
	for _, tt := range tests {
		got := ParseLogLevel(tt.input)
		if got != tt.want {
			t.Errorf("ParseLogLevel(%q) = %v, want %v", tt.input, got, tt.want)
		}
	}
}

func TestResolveAgent_ExplicitAgent(t *testing.T) {
	// When agent is explicitly set (not auto), ResolveAgent returns it as-is.
	cfg := &Config{Agent: types.AgentCodex}
	err := cfg.ResolveAgent(context.Background(), func(string) (string, error) {
		t.Fatal("lookPath should not be called for explicit agent")
		return "", nil
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentCodex {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentCodex)
	}
}

func TestResolveAgent_ExplicitACPAgent(t *testing.T) {
	cfg := &Config{Agent: "acp:gemini"}
	err := cfg.ResolveAgent(context.Background(), func(string) (string, error) {
		t.Fatal("lookPath should not be called for explicit acp agent")
		return "", nil
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != "acp:gemini" {
		t.Errorf("agent = %q, want %q", cfg.Agent, "acp:gemini")
	}
}

func TestResolveAgent_ExplicitRemovedAgentsRejected(t *testing.T) {
	for _, name := range []types.AgentName{"rovodev", "opencode"} {
		t.Run(string(name), func(t *testing.T) {
			cfg := &Config{Agent: name}
			err := cfg.ResolveAgent(context.Background(), func(string) (string, error) {
				t.Fatal("lookPath should not be called for removed explicit agent")
				return "", nil
			})
			if err == nil {
				t.Fatalf("ResolveAgent accepted removed agent %q", name)
			}
			if !strings.Contains(err.Error(), "valid options: auto, claude, codex, pi, copilot, droid, acp:<target>") {
				t.Fatalf("error = %v", err)
			}
		})
	}
}

func TestLoadGlobal_ACPConfig(t *testing.T) {
	path := filepath.Join(t.TempDir(), "config.yaml")
	data := []byte(`agent: acp:gemini
acpx_path: /opt/bin/acpx
acp_registry_overrides:
  local-gemini: node /tmp/mock-acp.mjs
`)
	if err := os.WriteFile(path, data, 0o644); err != nil {
		t.Fatalf("write config: %v", err)
	}

	cfg, err := LoadGlobal(path)
	if err != nil {
		t.Fatalf("LoadGlobal() error = %v", err)
	}
	if cfg.Agent != "acp:gemini" {
		t.Errorf("agent = %q, want acp:gemini", cfg.Agent)
	}
	if cfg.ACPXPath != "/opt/bin/acpx" {
		t.Errorf("ACPXPath = %q, want /opt/bin/acpx", cfg.ACPXPath)
	}
	if got := cfg.ACPRegistryOverrides["local-gemini"]; got != "node /tmp/mock-acp.mjs" {
		t.Errorf("ACPRegistryOverrides[local-gemini] = %q", got)
	}
}

func TestResolveAgent_AutoPicksFirstAvailable(t *testing.T) {
	cfg := &Config{Agent: types.AgentAuto}
	// Simulate: claude not found, codex found
	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "codex" {
			return "/usr/bin/codex", nil
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentCodex {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentCodex)
	}
}

func TestResolveAgent_AutoPicksDroid(t *testing.T) {
	cfg := &Config{Agent: types.AgentAuto}
	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "droid" {
			return "/usr/bin/droid", nil
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentDroid {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentDroid)
	}
}

func TestResolveAgent_ListPicksFirstAvailableAndKeepsFallbacks(t *testing.T) {
	cfg := &Config{Agents: []types.AgentName{types.AgentClaude, types.AgentCodex, types.AgentPi}}

	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		switch bin {
		case "codex", "pi":
			return "/usr/bin/" + bin, nil
		default:
			return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
		}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentCodex {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentCodex)
	}
	want := []types.AgentName{types.AgentCodex, types.AgentPi}
	if len(cfg.Agents) != len(want) {
		t.Fatalf("agents = %v, want %v", cfg.Agents, want)
	}
	for i := range want {
		if cfg.Agents[i] != want[i] {
			t.Fatalf("agents = %v, want %v", cfg.Agents, want)
		}
	}
}

func TestResolveAgent_ListSkipsUnavailableAuto(t *testing.T) {
	cfg := &Config{Agents: []types.AgentName{types.AgentAuto, "acp:gemini"}}

	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "acpx" {
			return "/usr/bin/acpx", nil
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != "acp:gemini" {
		t.Errorf("agent = %q, want acp:gemini", cfg.Agent)
	}
	if len(cfg.Agents) != 1 || cfg.Agents[0] != "acp:gemini" {
		t.Fatalf("agents = %v, want [acp:gemini]", cfg.Agents)
	}
}

func TestResolveAgent_AutoPicksClaude(t *testing.T) {
	cfg := &Config{Agent: types.AgentAuto}
	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "claude" {
			return "/usr/bin/claude", nil
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentClaude {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentClaude)
	}
}

func TestResolveAgent_AutoRespectsPathOverride(t *testing.T) {
	cfg := &Config{
		Agent:             types.AgentAuto,
		AgentPathOverride: map[string]string{"pi": "/custom/pi"},
	}
	// Only pi override path exists.
	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "/custom/pi" {
			return "/custom/pi", nil
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentPi {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentPi)
	}
}

func TestResolveAgent_AutoSkipsMissingOverrideAndFallsBack(t *testing.T) {
	cfg := &Config{
		Agent:             types.AgentAuto,
		AgentPathOverride: map[string]string{"claude": "/custom/claude"},
	}

	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		switch bin {
		case "/custom/claude":
			return "", &exec.Error{Name: bin, Err: fs.ErrNotExist}
		case "codex":
			return "/usr/bin/codex", nil
		default:
			return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
		}
	})

	if err != nil {
		t.Fatalf("unexpected error: %v", err)
	}
	if cfg.Agent != types.AgentCodex {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentCodex)
	}
}

func TestResolveAgent_AutoReturnsOverrideProbeError(t *testing.T) {
	cfg := &Config{
		Agent:             types.AgentAuto,
		AgentPathOverride: map[string]string{"claude": "/custom/claude"},
	}
	wantErr := &exec.Error{Name: "/custom/claude", Err: fs.ErrPermission}

	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		if bin == "/custom/claude" {
			return "", wantErr
		}
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})

	if !errors.Is(err, fs.ErrPermission) {
		t.Fatalf("expected permission error, got %v", err)
	}
	if cfg.Agent != types.AgentAuto {
		t.Errorf("agent = %q, want %q", cfg.Agent, types.AgentAuto)
	}
}

func TestResolveAgent_AutoNoneAvailable(t *testing.T) {
	cfg := &Config{Agent: types.AgentAuto}
	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})
	if err == nil {
		t.Fatal("expected error when no agents found")
	}
	if !strings.Contains(err.Error(), "no supported agent found") {
		t.Errorf("expected 'no supported agent found' in error, got: %v", err)
	}
	if !strings.Contains(err.Error(), "config") {
		t.Errorf("expected config guidance in error, got: %v", err)
	}
}

func TestResolveAgent_AutoNoneAvailableIncludesOverridePaths(t *testing.T) {
	cfg := &Config{
		Agent: types.AgentAuto,
		AgentPathOverride: map[string]string{
			"claude": "/custom/claude",
			"pi":     "/custom/pi",
		},
	}

	err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
		return "", &exec.Error{Name: bin, Err: exec.ErrNotFound}
	})

	if err == nil {
		t.Fatal("expected error when no agents found")
	}
	for _, want := range []string{"/custom/claude", "/custom/pi"} {
		if !strings.Contains(err.Error(), want) {
			t.Errorf("expected error to mention %q, got: %v", want, err)
		}
	}
}

func TestResolveAgent_ListRejectsRemovedAgents(t *testing.T) {
	for _, name := range []types.AgentName{"rovodev", "opencode"} {
		t.Run(string(name), func(t *testing.T) {
			cfg := &Config{Agents: []types.AgentName{name, types.AgentCodex}}
			err := cfg.ResolveAgent(context.Background(), func(bin string) (string, error) {
				return "/usr/bin/" + bin, nil
			})
			if err == nil {
				t.Fatalf("ResolveAgent accepted removed agent %q", name)
			}
			if !strings.Contains(err.Error(), "valid options: auto, claude, codex, pi, copilot, droid, acp:<target>") {
				t.Fatalf("error = %v", err)
			}
		})
	}
}
