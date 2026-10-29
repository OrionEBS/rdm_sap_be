interface Props {
  checked: boolean;
  disabled?: boolean;
  label?: string;
  onChange: (checked: boolean) => void;
}

/** Công tắc bật / tắt (role="switch" cho trình đọc màn hình). */
export default function Toggle({ checked, disabled, label, onChange }: Props) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      className={`toggle ${checked ? 'on' : 'off'}`}
      disabled={disabled}
      onClick={() => onChange(!checked)}
    >
      <span className="toggle-track">
        <span className="toggle-thumb" />
      </span>
      {label && <span className="toggle-label">{label}</span>}
    </button>
  );
}
