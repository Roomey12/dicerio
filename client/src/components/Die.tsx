import { memo } from "react";

const PIP_LAYOUT: Record<number, ReadonlyArray<readonly [number, number]>> = {
  1: [[1, 1]],
  2: [
    [0, 0],
    [2, 2],
  ],
  3: [
    [0, 0],
    [1, 1],
    [2, 2],
  ],
  4: [
    [0, 0],
    [0, 2],
    [2, 0],
    [2, 2],
  ],
  5: [
    [0, 0],
    [0, 2],
    [1, 1],
    [2, 0],
    [2, 2],
  ],
  6: [
    [0, 0],
    [0, 2],
    [1, 0],
    [1, 2],
    [2, 0],
    [2, 2],
  ],
};

interface DieProps {
  value: number;
  locked: boolean;
  selected: boolean;
  disabled: boolean;
  empty: boolean;
  bust?: boolean;
  onClick?: () => void;
}

function DieView({ value, locked, selected, disabled, empty, bust, onClick }: DieProps) {
  const interactive = !disabled && !locked && !empty && !bust && onClick;
  const pips = empty || value < 1 || value > 6 ? [] : PIP_LAYOUT[value];
  const visible = new Set(pips.map(([r, c]) => `${r}:${c}`));

  return (
    <div
      role="button"
      tabIndex={interactive ? 0 : -1}
      data-empty={empty}
      data-locked={locked}
      data-selected={selected}
      data-disabled={disabled}
      data-bust={bust ? true : undefined}
      className="die"
      onClick={interactive ? onClick : undefined}
      onKeyDown={
        interactive
          ? (event) => {
              if (event.key === "Enter" || event.key === " ") {
                event.preventDefault();
                onClick?.();
              }
            }
          : undefined
      }
      aria-label={empty ? "empty die" : `die showing ${value}${locked ? ", locked" : ""}`}
    >
      {empty ? (
        "·"
      ) : (
        <div className="dotface">
          {Array.from({ length: 9 }).map((_, idx) => {
            const r = Math.floor(idx / 3);
            const c = idx % 3;
            return (
              <span
                key={idx}
                className="pip"
                style={{ visibility: visible.has(`${r}:${c}`) ? "visible" : "hidden" }}
              />
            );
          })}
        </div>
      )}
    </div>
  );
}

export const Die = memo(DieView);
