package agent

import (
	"context"
	"os"
	"path/filepath"
	"reflect"
	"runtime"
	"strings"
	"testing"
)

func TestDroidAgent_BuildArgs(t *testing.T) {
	a := &droidAgent{}

	got := a.buildArgs("do work")
	want := []string{"exec", "do work", "-o", "json", "--auto", "high"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("args = %v, want %v", got, want)
	}
}

func TestDroidAgent_BuildArgs_ExtraArgsFirst(t *testing.T) {
	a := &droidAgent{extraArgs: []string{"--model", "gpt-5-codex", "--reasoning-effort", "high"}}

	got := a.buildArgs("do work")
	want := []string{"exec", "--model", "gpt-5-codex", "--reasoning-effort", "high", "do work", "-o", "json", "--auto", "high"}
	if !reflect.DeepEqual(got, want) {
		t.Fatalf("args = %v, want %v", got, want)
	}
}

func TestDroidAgent_BuildArgs_UserAutoSuppressesDefault(t *testing.T) {
	tests := [][]string{
		{"--auto", "medium"},
		{"--auto=medium"},
		{"--skip-permissions-unsafe"},
	}
	for _, extra := range tests {
		t.Run(strings.Join(extra, "_"), func(t *testing.T) {
			a := &droidAgent{extraArgs: extra}
			args := a.buildArgs("do work")
			joined := strings.Join(args, "\x00")
			if strings.Contains(joined, "\x00--auto\x00high") {
				t.Fatalf("args = %v, should not include default --auto high", args)
			}
		})
	}
}

func TestBuildDroidPrompt_InlinesSchema(t *testing.T) {
	schema := []byte(`{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}`)
	prompt := buildDroidPrompt("do work", schema)

	if !strings.Contains(prompt, "do work") {
		t.Fatalf("prompt missing original text: %q", prompt)
	}
	if !strings.Contains(prompt, "valid JSON matching this JSON Schema") {
		t.Fatalf("prompt missing JSON contract: %q", prompt)
	}
	if !strings.Contains(prompt, `"ok"`) {
		t.Fatalf("prompt missing schema: %q", prompt)
	}
}

func TestParseDroidResult_Success(t *testing.T) {
	out := []byte(`{"type":"result","subtype":"success","is_error":false,"duration_ms":1,"num_turns":1,"result":"{\"ok\":true}","session_id":"s1"}`)

	text, err := parseDroidResult(out)
	if err != nil {
		t.Fatalf("parseDroidResult() error = %v", err)
	}
	if text != `{"ok":true}` {
		t.Fatalf("text = %q, want JSON result", text)
	}
}

func TestParseDroidResult_ObjectResult(t *testing.T) {
	out := []byte(`{"type":"result","subtype":"success","is_error":false,"result":{"ok":true}}`)

	text, err := parseDroidResult(out)
	if err != nil {
		t.Fatalf("parseDroidResult() error = %v", err)
	}
	if text != `{"ok":true}` {
		t.Fatalf("text = %q, want marshaled object result", text)
	}
}

func TestParseDroidResult_Error(t *testing.T) {
	out := []byte(`{"type":"result","subtype":"error","is_error":true,"result":"permission denied"}`)

	_, err := parseDroidResult(out)
	if err == nil {
		t.Fatal("expected error")
	}
	if !strings.Contains(err.Error(), "permission denied") {
		t.Fatalf("error = %v, want result detail", err)
	}
}

func TestDroidAgent_RunParsesJSONOutput(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("shell fixture is Unix-only")
	}
	dir := t.TempDir()
	bin := writeFakeDroid(t, dir, `#!/bin/sh
printf '%s\n' '{"type":"result","subtype":"success","is_error":false,"result":"{\"ok\":true}"}'
`)
	a := &droidAgent{bin: bin}

	result, err := a.Run(context.Background(), RunOpts{
		Prompt:     "do work",
		CWD:        dir,
		JSONSchema: []byte(`{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}`),
	})
	if err != nil {
		t.Fatalf("Run() error = %v", err)
	}
	if string(result.Output) != `{"ok":true}` {
		t.Fatalf("output = %s, want JSON object", result.Output)
	}
}

func TestDroidAgent_RunIncludesStderrOnExitFailure(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("shell fixture is Unix-only")
	}
	dir := t.TempDir()
	bin := writeFakeDroid(t, dir, `#!/bin/sh
printf '%s\n' '{"type":"result","subtype":"error","is_error":true,"result":"not authenticated"}'
printf '%s\n' 'FACTORY_API_KEY missing' >&2
exit 1
`)
	a := &droidAgent{bin: bin}

	_, err := a.Run(context.Background(), RunOpts{Prompt: "do work", CWD: dir})
	if err == nil {
		t.Fatal("expected error")
	}
	if !strings.Contains(err.Error(), "not authenticated") || !strings.Contains(err.Error(), "FACTORY_API_KEY missing") {
		t.Fatalf("error = %v, want stdout and stderr details", err)
	}
}

func writeFakeDroid(t *testing.T, dir, script string) string {
	t.Helper()
	path := filepath.Join(dir, "droid")
	if err := os.WriteFile(path, []byte(script), 0o755); err != nil {
		t.Fatalf("write fake droid: %v", err)
	}
	return path
}
