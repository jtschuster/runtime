#!/bin/bash
INPUT=$(cat)

# Only process bash tool invocations
[[ "$INPUT" == *'"toolName"'*'"bash"'* ]] || exit 0

# Detect git push at command boundaries:
#   - After the "command" JSON key (start of command)
#   - After shell separators (&&, ||, ;)
PUSH=false
echo "$INPUT" | grep -qE 'command[\\": ]+git\s+push' && PUSH=true
echo "$INPUT" | grep -qE '(&&|;|\|\|)\s*git\s+push' && PUSH=true

if $PUSH; then
    if [[ "$INPUT" == *INTERRUPT_USER_FOR_PERMISSION* ]]; then
        echo '{"permissionDecision":"ask","permissionDecisionReason":"git push requires user approval."}'
    else
        echo '{"permissionDecision":"deny","permissionDecisionReason":"git push is blocked by default. Retry with INTERRUPT_USER_FOR_PERMISSION in the command if this push is truly necessary, otherwise skip it."}'
    fi
fi
