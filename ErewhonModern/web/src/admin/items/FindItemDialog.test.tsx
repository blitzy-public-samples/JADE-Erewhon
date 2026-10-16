import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { UserEvent } from '@testing-library/user-event';
import { api } from '../../api/client';
import type { Id, ItemDetailsResponse, ItemListRow } from '../../api/types';
import { msg } from '../../messages/messages';
import { renderWithSession, useFixture } from '../../test/fixtures';
import { FindItemDialog } from './FindItemDialog';

const FIND_ITEM_CAPTION = 'Find Item';
const ITEM_CODE_CAPTION = 'Item Code';
const OK_CAPTION = 'OK';

describe('FindItemDialog', () => {
  const fixture = useFixture('AdminGuards');

  function fixtureId(key: 'retailItem' | 'otherRetailItem'): Id {
    const id = fixture.ids[key];
    if (id === undefined) {
      throw new Error(`The AdminGuards fixture returned no ${key} id`);
    }
    return id;
  }

  async function itemCode(id: Id): Promise<string> {
    const details = await api.get<ItemDetailsResponse>(`/api/app/items/${id}`);
    const { code } = details.item;
    if (code === null || code === '') {
      throw new Error(`Item ${id} has no code`);
    }
    return code;
  }

  function renderDialog(): {
    user: UserEvent;
    onFound: Mock<(row: ItemListRow) => void>;
    onClose: Mock<() => void>;
  } {
    const user = userEvent.setup();
    const onFound = vi.fn<(row: ItemListRow) => void>();
    const onClose = vi.fn<() => void>();
    renderWithSession(<FindItemDialog onFound={onFound} onClose={onClose} />, fixture);
    return { user, onFound, onClose };
  }

  function findItemForm(): HTMLElement {
    return screen.getByRole('dialog', { name: FIND_ITEM_CAPTION });
  }

  async function enterCode(user: UserEvent, code: string): Promise<void> {
    const form = findItemForm();
    if (code !== '') {
      await user.type(within(form).getByLabelText(ITEM_CODE_CAPTION), code);
    }
    await user.click(within(form).getByRole('button', { name: OK_CAPTION }));
  }

  it('reports another agent item as not found', async () => {
    const code = await itemCode(fixtureId('otherRetailItem'));
    const { user, onFound, onClose } = renderDialog();

    await enterCode(user, code);

    const message = await screen.findByRole('dialog', { name: msg('NotFound') });
    expect(message).toHaveAccessibleDescription(msg('ItemNotFound'));
    expect(screen.queryByRole('dialog', { name: FIND_ITEM_CAPTION })).not.toBeInTheDocument();
    expect(onFound).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();

    await user.click(within(message).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(onClose).toHaveBeenCalledTimes(1);
    });
    expect(onFound).not.toHaveBeenCalled();
    expect(screen.queryByRole('dialog', { name: msg('NotFound') })).not.toBeInTheDocument();
  });

  it('returns an own item', async () => {
    const id = fixtureId('retailItem');
    const code = await itemCode(id);
    const { user, onFound, onClose } = renderDialog();

    await enterCode(user, code);

    await waitFor(() => {
      expect(onFound).toHaveBeenCalledTimes(1);
    });
    const [row] = onFound.mock.calls[0] ?? [];
    expect(row?.item.id).toBe(id);
    expect(row?.item.code).toBe(code);
    expect(onClose).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog', { name: msg('NotFound') })).not.toBeInTheDocument();
  });

  it('requires an item code', async () => {
    const { user, onFound, onClose } = renderDialog();

    await enterCode(user, '');

    const message = await screen.findByRole('dialog', { name: msg('InvalidCode') });
    expect(message).toHaveAccessibleDescription(msg('ItemNumberRequired'));
    expect(findItemForm()).toBeInTheDocument();
    expect(onClose).not.toHaveBeenCalled();

    await user.click(within(message).getByRole('button', { name: OK_CAPTION }));

    await waitFor(() => {
      expect(screen.queryByRole('dialog', { name: msg('InvalidCode') })).not.toBeInTheDocument();
    });
    expect(findItemForm()).toBeInTheDocument();
    expect(onFound).not.toHaveBeenCalled();
    expect(onClose).not.toHaveBeenCalled();
  });
});
