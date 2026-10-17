import translate from 'Utilities/String/translate';

const importModeOptions = [
  { key: 'move', value: () => translate('MoveFiles') },
  { key: 'copy', value: () => translate('HardlinkCopyFiles') },
  // TaleArr: a full copy even when "Use Hardlinks instead of Copy" is on.
  { key: 'plainCopy', value: 'Copy Files (no hardlinks)' }
];

// TaleArr: keep any stored mode the select knows; anything else is the hardlink/copy default.
export function normalizeImportMode(importMode) {
  return importModeOptions.some((option) => option.key === importMode) ? importMode : 'copy';
}

// TaleArr: the API only knows move, copy and auto. A plain copy is sent as copy plus the
// additive talearrTransfer field, which stock Chaptarr ignores.
export function getImportCommandMode(importMode) {
  if (importMode === 'plainCopy') {
    return { importMode: 'copy', talearrTransfer: 'copy' };
  }

  return { importMode };
}

export default importModeOptions;
