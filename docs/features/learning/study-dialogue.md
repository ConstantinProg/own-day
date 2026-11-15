# Study invitations in learning dialogue

Enrolled learning dialogue accepts explicit phrases using the existing command
use cases and durable Learning queue:

- `напоминай мне Europe/Moscow | 1 | 18:00-20:00` explicitly configures opt-in;
  zone, frequency and windows use the same validation as `/learn_schedule`.
- `расписание занятий` reads settings without creating a subscription.
- `не присылай приглашения` pauses notifications and invalidates pending consent
  to act on an invitation; `возобнови приглашения` resumes an existing schedule.
- `покажи повторения` reads existing due reviews.
- `начать приглашение <код>`, `отложить приглашение <код>` and
  `пропустить приглашение <код>` use the invitation ID shown in the message.

Bare yes/later does not select an invitation. Expired, foreign and consumed IDs
remain subject to existing owner/currentness checks. Starting material stays in
the prepare/provider/apply lane; schedule mutation, operation marker, replies and
completion commit together. No LLM interprets a schedule or chooses opt-in defaults.
An active Inbox/language editor retains priority. Existing slash commands remain.

No new schema or delivery topology is introduced. Existing Domain DST/window rules
and dispatcher transaction locks remain authoritative. Immediately before invitation
send the adapter rechecks opt-out/currentness under the learner lock. Concurrent
invitation dispatchers serialize through that lock. Outbox delivery still requires
one active deliverer across replicas: ordinary replies do not have durable delivery
claims. Claim/lease migration and recovery/fencing tests are required before
scale-out; this feature does not authorize scale-out or production deployment.

Telegram Desktop/NVDA and real operational timing remain manual release checks.
