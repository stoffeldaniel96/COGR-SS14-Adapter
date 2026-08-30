# COGR Component Localization

## Examination messages for COGR-controlled entities

# Shown when the entity is actively controlled by COGR runtime
cogr-examined-controlled = { CAPITALIZE(SUBJECT($ent)) } { CONJUGATE-BE($ent) } being controlled by an external cognitive system.

# Shown when COGR runtime is disconnected
cogr-examined-disconnected = { CAPITALIZE(SUBJECT($ent)) } { CONJUGATE-HAVE($ent) } lost connection to { POSS-ADJ($ent) } cognitive system and stands motionless.

# Fallback for unknown state
cogr-examined-unknown = { CAPITALIZE(SUBJECT($ent)) } { CONJUGATE-BE($ent) } in an unknown cognitive state.

# Shown when entity is paused/suspended
cogr-examined-paused = { CAPITALIZE(SUBJECT($ent)) } { CONJUGATE-BE($ent) } currently suspended by { POSS-ADJ($ent) } cognitive system.
