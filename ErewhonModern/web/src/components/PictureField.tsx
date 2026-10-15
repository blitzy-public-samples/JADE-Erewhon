import { useEffect, useRef, useState } from 'react';
import type { ChangeEvent, CSSProperties, KeyboardEvent } from 'react';

interface PictureFieldProps {
  /** Base64 content of the picture chosen in this dialog, or null when none has been chosen. */
  base64: string | null;
  /** Receives the base64 content of each non-empty file the user chooses. */
  onChange: (base64: string | null) => void;
  /** Prevents a new picture from being chosen. */
  disabled?: boolean;
  /** Fetches the stored picture shown while `base64` is null. Called once per mount. */
  loadExisting?: () => Promise<Blob | null>;
}

interface Upload {
  base64: string;
  dataUrl: string;
}

interface DecodedPicture {
  base64: string;
  url: string | null;
}

const LOAD_CAPTION = 'Double-click to load a picture from a disk file.';
const PICTURE_SIZE = '12.5rem';
const PICTURE_SIZE_PX = 200;

const boxStyle: CSSProperties = {
  boxSizing: 'border-box',
  inlineSize: PICTURE_SIZE,
  blockSize: PICTURE_SIZE,
  border: '1px solid ButtonBorder',
  backgroundColor: 'Canvas',
  overflow: 'hidden',
};

const styles = {
  fieldset: {
    display: 'inline-flex',
    flexDirection: 'column',
    alignItems: 'center',
    gap: '0.5rem',
    margin: 0,
  },
  box: boxStyle,
  boxDisabled: { ...boxStyle, pointerEvents: 'none' },
  image: {
    display: 'block',
    inlineSize: '100%',
    blockSize: '100%',
    objectFit: 'contain',
  },
  caption: {
    maxInlineSize: PICTURE_SIZE,
    textAlign: 'center',
    overflowWrap: 'break-word',
  },
  fileInput: {
    display: 'none',
  },
} satisfies Record<string, CSSProperties>;

/** Returns an object URL for the blob, or null where the environment cannot create one for it. */
function createObjectUrl(blob: Blob): string | null {
  if (typeof URL.createObjectURL !== 'function') {
    return null;
  }
  try {
    return URL.createObjectURL(blob);
  } catch {
    return null;
  }
}

/** Releases an object URL created by `createObjectUrl`. */
function revokeObjectUrl(url: string | null): void {
  if (url !== null && typeof URL.revokeObjectURL === 'function') {
    URL.revokeObjectURL(url);
  }
}

/** Decodes base64 content into a blob, or returns null when the text is not valid base64. */
function decodeBase64(base64: string): Blob | null {
  try {
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let index = 0; index < binary.length; index += 1) {
      bytes[index] = binary.charCodeAt(index);
    }
    return new Blob([bytes]);
  } catch {
    return null;
  }
}

/**
 * The "Picture" group of the item editors.
 *
 * Shows the picture chosen in this dialog (`base64`) or, while none has been chosen, the stored
 * picture returned by `loadExisting`. Double-clicking the picture box, or pressing Enter or Space
 * on it, opens the file picker. A chosen non-empty file is read as base64 and passed to
 * `onChange`; an empty file leaves the picture unchanged.
 */
export function PictureField(props: PictureFieldProps) {
  const { base64, onChange, disabled = false, loadExisting } = props;

  const fileInputRef = useRef<HTMLInputElement>(null);
  const readerRef = useRef<FileReader | null>(null);
  const loadExistingRef = useRef(loadExisting);

  const [upload, setUpload] = useState<Upload | null>(null);
  const [decoded, setDecoded] = useState<DecodedPicture | null>(null);
  const [existingUrl, setExistingUrl] = useState<string | null>(null);

  const showsUpload = base64 !== null && upload !== null && upload.base64 === base64;

  useEffect(() => {
    const load = loadExistingRef.current;
    if (load === undefined) {
      return undefined;
    }
    let active = true;
    let url: string | null = null;
    Promise.resolve()
      .then(() => load())
      .then(
        (blob) => {
          if (!active || blob === null || blob.size === 0) {
            return;
          }
          url = createObjectUrl(blob);
          setExistingUrl(url);
        },
        () => undefined,
      );
    return () => {
      active = false;
      revokeObjectUrl(url);
    };
  }, []);

  useEffect(() => {
    if (base64 === null || showsUpload) {
      return undefined;
    }
    const blob = decodeBase64(base64);
    const url = blob === null || blob.size === 0 ? null : createObjectUrl(blob);
    setDecoded({ base64, url });
    return () => {
      revokeObjectUrl(url);
      setDecoded((current) => (current !== null && current.base64 === base64 ? null : current));
    };
  }, [base64, showsUpload]);

  useEffect(
    () => () => {
      readerRef.current?.abort();
      readerRef.current = null;
    },
    [],
  );

  let previewUrl: string | null = null;
  if (base64 === null) {
    previewUrl = existingUrl;
  } else if (showsUpload) {
    previewUrl = upload.dataUrl;
  } else if (decoded !== null && decoded.base64 === base64) {
    previewUrl = decoded.url;
  }

  function openPicker(): void {
    if (!disabled) {
      fileInputRef.current?.click();
    }
  }

  function handleKeyDown(event: KeyboardEvent<HTMLDivElement>): void {
    if (event.key === 'Enter' || event.key === ' ') {
      event.preventDefault();
      openPicker();
    }
  }

  function handleFileChange(event: ChangeEvent<HTMLInputElement>): void {
    const input = event.target;
    const file = input.files?.[0];
    input.value = '';
    if (file === undefined || file.size === 0) {
      return;
    }

    readerRef.current?.abort();
    const reader = new FileReader();
    readerRef.current = reader;
    reader.onload = () => {
      if (readerRef.current !== reader) {
        return;
      }
      readerRef.current = null;
      if (typeof reader.result !== 'string') {
        return;
      }
      const dataUrl = reader.result;
      const content = dataUrl.slice(dataUrl.indexOf(',') + 1);
      setUpload({ base64: content, dataUrl });
      onChange(content);
    };
    reader.onerror = () => {
      if (readerRef.current === reader) {
        readerRef.current = null;
      }
    };
    reader.readAsDataURL(file);
  }

  return (
    <fieldset style={styles.fieldset}>
      <legend>Picture</legend>
      <div
        role="button"
        tabIndex={disabled ? undefined : 0}
        aria-disabled={disabled ? true : undefined}
        title={LOAD_CAPTION}
        style={disabled ? styles.boxDisabled : styles.box}
        onDoubleClick={openPicker}
        onKeyDown={handleKeyDown}
      >
        {previewUrl !== null && (
          <img
            src={previewUrl}
            alt="Picture"
            width={PICTURE_SIZE_PX}
            height={PICTURE_SIZE_PX}
            style={styles.image}
          />
        )}
      </div>
      <span style={styles.caption}>{LOAD_CAPTION}</span>
      <input
        ref={fileInputRef}
        type="file"
        aria-label="Picture"
        style={styles.fileInput}
        disabled={disabled}
        onChange={handleFileChange}
      />
    </fieldset>
  );
}
