import type { CSSProperties, ReactElement } from 'react';
import type { OperationResult } from '../api/types';
import { msg } from '../messages/messages';
import type { MessageBoxOptions } from './MessageBox';

const ERROR_LINE_SEPARATOR = '\r\n';

const ERROR_TEXT_STYLE: CSSProperties = {
  whiteSpace: 'pre-wrap',
  overflowWrap: 'break-word',
  color: 'rgb(192, 0, 0)',
};

/**
 * Returns the full error text of `result`: its `fullErrorText` when that is a non-empty string, otherwise its
 * `errors` in their order, each one after the first non-empty text preceded by CR LF; the empty string when there
 * is no error text. Texts are neither trimmed nor de-duplicated.
 */
export function fullErrorText(result: OperationResult): string {
  if (typeof result.fullErrorText === 'string' && result.fullErrorText !== '') {
    return result.fullErrorText;
  }

  const errors: readonly string[] = result.errors ?? [];
  let text = '';
  for (const error of errors) {
    if (text !== '') {
      text += ERROR_LINE_SEPARATOR;
    }
    text += error;
  }
  return text;
}

/** Properties of {@link ErrorList}. */
export interface ErrorListProps {
  /** Result whose full error text is shown. */
  result: OperationResult;
}

/**
 * Renders the full error text of `result` unchanged as one dark red `role="alert"` text block whose CR LF breaks
 * show as lines, or nothing when the text is empty.
 */
export function ErrorList({ result }: ErrorListProps): ReactElement | null {
  const text = fullErrorText(result);
  if (text === '') {
    return null;
  }

  return (
    <div role="alert" style={ERROR_TEXT_STYLE}>
      {text}
    </div>
  );
}

/**
 * Returns the OK-only message box with the information icon that shows the full error text of `result`, titled
 * `title`, or `$ErrorDuringSave` when `title` is omitted, null or empty.
 */
export function displayErrorsOptions(result: OperationResult, title?: string | null): MessageBoxOptions {
  return {
    title: title || msg('ErrorDuringSave'),
    message: fullErrorText(result),
    icon: 'information',
    buttons: 'ok',
  };
}
