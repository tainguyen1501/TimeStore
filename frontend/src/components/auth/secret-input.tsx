import { useState } from "react"
import { Lock } from "lucide-react"

import { Icon } from "@/components/shared/icons"
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from "@/components/ui/input-group"

/** Secret key field with a lock icon and a show/hide toggle. */
export function SecretInput(props: React.ComponentProps<typeof InputGroupInput>) {
  const [visible, setVisible] = useState(false)
  return (
    <InputGroup>
      <InputGroupAddon>
        <Lock />
      </InputGroupAddon>
      <InputGroupInput {...props} type={visible ? "text" : "password"} className="font-mono" />
      <InputGroupAddon align="inline-end">
        <InputGroupButton
          size="icon-xs"
          aria-label={visible ? "Hide secret key" : "Show secret key"}
          onClick={() => setVisible((v) => !v)}
        >
          <Icon name={visible ? "hide" : "show"} />
        </InputGroupButton>
      </InputGroupAddon>
    </InputGroup>
  )
}
