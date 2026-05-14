# AGENTS.md

This file defines project-wide guidance for code generation in this repository.

## Global policy
- Prefer minimal, local changes over broad refactors unless explicitly requested.
- Preserve existing code style and naming conventions in the touched area.
- Before changing rendering behavior, prefer checking how the current scene or pipeline already provides the needed data.
- If some type can be inferenced, use `var`.
- Use declarative way(LINQ), instead of imperative way. The only exception is performance-critial part(like Update()).
- If `private` is default, do not mention it explicitly.
- If null has not special meaning in the context, do not check null just for validation. 
- Early exception(crash) is always preferred than silent fail.
- Do not make state(or field) or cache of which can be calculated or queried. The only exception is performance critical part.
- Use just public, instead of private [serializefield].
- `{get; set;}` is prefered than `get_position()` and `set_position()`. If we are just wrapping something, just expose it as public.
- Default parameter of methods or functions are prohibited. Ask first.
- No Enum arithmetic. No adding, subtraction, comparing is not allowed between enums. Only equality comparison is allowed. 

# UI-toolkit
- Dedicated stylesheet is preferred than inline styling. 
- If some style is repeated, consider makeing common class or promoting it to global style(styles.uss)

## naming
- snake_case at every identifiers(class, method, field, variable, enum...).

## style
- Open brace in same line

## Safety
- Always show plan before excute. The plan must includes scope of change.
- If a requested change appears to require a wider pipeline or architecture change, stop and ask before expanding scope.
- Do not remove or overwrite unrelated user changes.
- If verification was not run, state that clearly in the final response.