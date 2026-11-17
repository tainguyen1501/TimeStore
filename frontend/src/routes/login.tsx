import { zodResolver } from "@hookform/resolvers/zod"
import { createFileRoute, redirect, useNavigate } from "@tanstack/react-router"
import { Database, KeyRound } from "lucide-react"
import { useState } from "react"
import { useForm } from "react-hook-form"
import { z } from "zod"

import { AuthError, AuthLayout } from "@/components/auth/auth-layout"
import { SecretInput } from "@/components/auth/secret-input"
import { SubmitButton } from "@/components/auth/submit-button"
import {
  Field,
  FieldDescription,
  FieldError,
  FieldGroup,
  FieldLabel,
  FieldSet,
} from "@/components/ui/field"
import { InputGroup, InputGroupAddon, InputGroupInput } from "@/components/ui/input-group"
import { meQuery, useLogin } from "@/hooks/api/use-auth"
import { useCountdown } from "@/hooks/use-countdown"
import { getErrorCode, getErrorMessage, getRetryAfterSeconds } from "@/lib/api-response"
import { safeRedirect } from "@/lib/redirect"

/** Server error codes this page branches on (TimeStore.Application AuthErrors.Codes). */
const TOO_MANY_ATTEMPTS = "AUTH_TOO_MANY_ATTEMPTS"

export const Route = createFileRoute("/login")({
  validateSearch: z.object({ redirect: z.string().optional() }),
  beforeLoad: async ({ context: { queryClient }, search }) => {
    // Already signed in: go where the user was heading.
    const session = await queryClient.ensureQueryData(meQuery).catch(() => null)
    if (session) throw redirect({ href: safeRedirect(search.redirect) })
  },
  component: LoginPage,
})

const loginSchema = z.object({
  accessKeyId: z.string().trim().min(1, "Enter your access key."),
  secretKey: z.string().min(1, "Enter your secret key."),
})

type LoginForm = z.infer<typeof loginSchema>

function LoginPage() {
  const { redirect: redirectTo } = Route.useSearch()
  const navigate = useNavigate()
  const login = useLogin({ meta: { skipErrorToast: true } })
  const throttle = useCountdown()
  const [apiError, setApiError] = useState<string | null>(null)
  const form = useForm<LoginForm>({
    resolver: zodResolver(loginSchema),
    defaultValues: { accessKeyId: "", secretKey: "" },
  })
  const { errors } = form.formState

  const onSubmit = form.handleSubmit((values) => {
    setApiError(null)
    login.mutate(values, {
      onSuccess: () => void navigate({ href: safeRedirect(redirectTo), replace: true }),
      onError: (error) => {
        const retryAfter = getRetryAfterSeconds(error)
        if (getErrorCode(error) === TOO_MANY_ATTEMPTS && retryAfter) {
          throttle.start(retryAfter)
          return
        }
        setApiError(getErrorMessage(error))
        // The secret is wrong or the key may not sign in: let the user type it again.
        form.resetField("secretKey")
        form.setFocus("secretKey")
      },
    })
  })

  // While throttled the countdown replaces any other message; the Server would refuse the attempt anyway.
  const message =
    throttle.remaining > 0
      ? `Too many failed sign-ins. Try again in ${throttle.remaining} s.`
      : apiError

  return (
    <AuthLayout
      icon={Database}
      title="Sign in to TimeStore"
      description="Use an access key with the admin or viewer role."
    >
      <AuthError message={message} />

      <form onSubmit={onSubmit} noValidate>
        <FieldSet disabled={login.isPending}>
          <FieldGroup className="gap-4">
            <Field data-invalid={Boolean(errors.accessKeyId)}>
              <FieldLabel htmlFor="accessKeyId">Access key</FieldLabel>
              <InputGroup>
                <InputGroupAddon>
                  <KeyRound />
                </InputGroupAddon>
                <InputGroupInput
                  id="accessKeyId"
                  autoComplete="username"
                  autoCapitalize="none"
                  spellCheck={false}
                  autoFocus
                  className="font-mono"
                  aria-invalid={Boolean(errors.accessKeyId)}
                  {...form.register("accessKeyId")}
                />
              </InputGroup>
              <FieldError errors={[errors.accessKeyId]} />
            </Field>

            <Field data-invalid={Boolean(errors.secretKey)}>
              <FieldLabel htmlFor="secretKey">Secret key</FieldLabel>
              <SecretInput
                id="secretKey"
                autoComplete="current-password"
                spellCheck={false}
                aria-invalid={Boolean(errors.secretKey)}
                {...form.register("secretKey")}
              />
              <FieldError errors={[errors.secretKey]} />
              <FieldDescription className="text-xs">
                The root key from the server configuration, or a key an admin gave a role.
              </FieldDescription>
            </Field>

            <SubmitButton
              pending={login.isPending}
              disabled={throttle.remaining > 0}
              label="Sign in"
              pendingLabel="Signing in…"
            />
          </FieldGroup>
        </FieldSet>
      </form>
    </AuthLayout>
  )
}
