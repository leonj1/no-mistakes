package agent

import (
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os/exec"
	"strings"
	"sync"

	"github.com/kunchenguid/no-mistakes/internal/shellenv"
)

// droidAgent spawns Factory's Droid CLI for each invocation. Droid's headless
// mode is `droid exec`; JSON output returns a single result object on stdout.
type droidAgent struct {
	bin       string
	extraArgs []string
}

func (a *droidAgent) Name() string { return "droid" }

func (a *droidAgent) Run(ctx context.Context, opts RunOpts) (*Result, error) {
	return runWithRetry(ctx, "droid", opts, claudeMaxRetries, classifyTransient, nil, func() (*Result, error) {
		return a.runOnce(ctx, opts)
	})
}

func (a *droidAgent) Close() error { return nil }

func (a *droidAgent) runOnce(ctx context.Context, opts RunOpts) (*Result, error) {
	prompt := buildDroidPrompt(opts.Prompt, opts.JSONSchema)
	args := a.buildArgs(prompt)
	cmd := exec.CommandContext(ctx, a.bin, args...)
	cmd.Dir = opts.CWD
	cmd.Stdin = nil
	cmd.Env = gitSafeEnv(opts.CWD)
	shellenv.ConfigureShellCommand(cmd)

	started, err := startNativeAgentCommand(cmd)
	if err != nil {
		return nil, fmt.Errorf("droid start: %w", err)
	}
	defer started.closePipes()

	var stdoutBuf, stderrBuf []byte
	var stdoutErr, stderrErr error
	var wg sync.WaitGroup
	wg.Add(2)
	go func() {
		defer wg.Done()
		stdoutBuf, stdoutErr = io.ReadAll(started.stdout)
	}()
	go func() {
		defer wg.Done()
		stderrBuf, stderrErr = io.ReadAll(started.stderr)
	}()

	waitErr := started.wait()
	wg.Wait()
	if stdoutErr != nil {
		return nil, fmt.Errorf("droid read stdout: %w", stdoutErr)
	}
	if stderrErr != nil {
		return nil, fmt.Errorf("droid read stderr: %w", stderrErr)
	}

	text, parseErr := parseDroidResult(stdoutBuf)
	if waitErr != nil {
		return nil, droidExitError(waitErr, parseErr, stdoutBuf, stderrBuf)
	}
	if parseErr != nil {
		return nil, fmt.Errorf("droid parse result: %w", parseErr)
	}
	if opts.OnChunk != nil {
		opts.OnChunk(text)
	}
	return finalizeTextResult("droid", text, opts.JSONSchema, TokenUsage{})
}

// buildArgs returns the Droid argv for one invocation. User extras come after
// `exec`, before the prompt and managed output flags, so options like --model
// and --reasoning-effort apply to the run. The default autonomy is high because
// no-mistakes steps may need to edit files, run commands, commit, and push.
func (a *droidAgent) buildArgs(prompt string) []string {
	args := make([]string, 0, len(a.extraArgs)+6)
	args = append(args, "exec")
	args = append(args, a.extraArgs...)
	args = append(args, prompt, "-o", "json")
	if !droidUserSetAutonomy(a.extraArgs) {
		args = append(args, "--auto", "high")
	}
	return args
}

func droidUserSetAutonomy(extraArgs []string) bool {
	for _, arg := range extraArgs {
		switch {
		case arg == "--auto", arg == "--skip-permissions-unsafe":
			return true
		case strings.HasPrefix(arg, "--auto="):
			return true
		}
	}
	return false
}

// buildDroidPrompt appends a JSON-output contract when structured output is
// requested. Droid's JSON output wraps the assistant's final text; it does not
// accept a JSON Schema flag like Codex.
func buildDroidPrompt(prompt string, schema json.RawMessage) string {
	if len(schema) == 0 {
		return prompt
	}
	pretty, err := json.MarshalIndent(json.RawMessage(schema), "", "  ")
	if err != nil {
		pretty = []byte(schema)
	}
	return prompt + "\n\n## no-mistakes final output contract\n\n" +
		"When the iteration is complete, your final assistant response must be only valid JSON matching this JSON Schema. " +
		"Do not wrap it in Markdown fences. Do not include prose before or after the JSON object.\n\n" +
		string(pretty)
}

type droidResult struct {
	Type    string          `json:"type"`
	Subtype string          `json:"subtype"`
	IsError bool            `json:"is_error"`
	Result  json.RawMessage `json:"result"`
	Error   string          `json:"error"`
	Message string          `json:"message"`
}

func parseDroidResult(data []byte) (string, error) {
	trimmed := strings.TrimSpace(string(data))
	if trimmed == "" {
		return "", fmt.Errorf("empty stdout")
	}

	var result droidResult
	if err := json.Unmarshal([]byte(trimmed), &result); err != nil {
		return "", err
	}

	text, err := droidResultText(result.Result)
	if err != nil {
		return "", err
	}
	if result.IsError || result.Subtype == "error" {
		detail := droidFirstNonEmpty(text, result.Error, result.Message, result.Subtype)
		return "", fmt.Errorf("droid reported error: %s", detail)
	}
	if text == "" {
		return "", fmt.Errorf("missing result text")
	}
	return text, nil
}

func droidResultText(raw json.RawMessage) (string, error) {
	if len(raw) == 0 || string(raw) == "null" {
		return "", nil
	}
	var text string
	if err := json.Unmarshal(raw, &text); err == nil {
		return text, nil
	}
	var value any
	if err := json.Unmarshal(raw, &value); err != nil {
		return "", err
	}
	encoded, err := json.Marshal(value)
	if err != nil {
		return "", err
	}
	return string(encoded), nil
}

func droidExitError(waitErr, parseErr error, stdout, stderr []byte) error {
	parts := []string{waitErr.Error()}
	if parseErr != nil {
		parts = append(parts, parseErr.Error())
	}
	if out := strings.TrimSpace(string(stdout)); out != "" && parseErr == nil {
		parts = append(parts, outputSnippet(out))
	}
	if errText := strings.TrimSpace(string(stderr)); errText != "" {
		parts = append(parts, errText)
	}
	return fmt.Errorf("droid exited: %s", strings.Join(parts, ": "))
}

func droidFirstNonEmpty(values ...string) string {
	for _, value := range values {
		if strings.TrimSpace(value) != "" {
			return strings.TrimSpace(value)
		}
	}
	return "unknown error"
}
