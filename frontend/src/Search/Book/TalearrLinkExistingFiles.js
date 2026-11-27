import PropTypes from 'prop-types';
import React from 'react';
import CheckInput from 'Components/Form/CheckInput';
import styles from './TalearrLinkExistingFiles.css';

const SOURCE_LABELS = {
  audiobookshelf: 'Audiobookshelf',
  authorFolder: 'Author Folder'
};

export function formatLinkDuration(seconds) {
  if (!seconds || seconds <= 0) {
    return null;
  }

  const totalMinutes = Math.round(seconds / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;

  return hours ? `${hours}h ${minutes}m` : `${minutes}m`;
}

function describeSource(candidate) {
  const label = SOURCE_LABELS[candidate.source] || 'Existing Files';

  if (candidate.source === 'audiobookshelf' && candidate.sourceName) {
    return `${label} (${candidate.sourceName})`;
  }

  return label;
}

// Talearr: Link Existing Files in the Add Book modal. Shown only when an existing audiobook
// (an Audiobookshelf item or a folder in the author's audiobook folder) looks like this book; the
// chosen folders are imported to the new audiobook with a normal Manual Import after the add.
function TalearrLinkExistingFiles(props) {
  const {
    candidates,
    selectedPaths,
    onToggle
  } = props;

  if (!candidates || !candidates.length) {
    return null;
  }

  return (
    <div className={styles.container}>
      <div className={styles.heading}>
        {'Link Existing Files'}
      </div>

      <div className={styles.help}>
        {'Import these files to the new audiobook instead of searching for it. Works like Manual Import: files outside your root folders are hardlinked or copied, the originals stay where they are.'}
      </div>

      {
        candidates.map((candidate) => {
          const duration = formatLinkDuration(candidate.durationSeconds);
          const isSelected = selectedPaths.includes(candidate.path);
          const meta = [
            describeSource(candidate),
            duration,
            `${candidate.fileCount} ${candidate.fileCount === 1 ? 'file' : 'files'}`,
            candidate.narrators ? `Narrated by ${candidate.narrators}` : null,
            candidate.strong ? 'Strong match' : 'Possible match'
          ].filter(Boolean).join(' · ');

          return (
            <label
              key={candidate.path}
              className={styles.candidate}
            >
              <CheckInput
                containerClassName={styles.checkContainer}
                className={styles.check}
                name={`linkExisting-${candidate.path}`}
                value={isSelected}
                onChange={() => onToggle(candidate.path)}
              />

              <div className={styles.details}>
                <div className={styles.title}>
                  {candidate.title || candidate.path}
                </div>

                <div className={styles.path}>
                  {candidate.path}
                </div>

                <div className={styles.meta}>
                  {meta}
                </div>
              </div>
            </label>
          );
        })
      }
    </div>
  );
}

TalearrLinkExistingFiles.propTypes = {
  candidates: PropTypes.arrayOf(PropTypes.object),
  selectedPaths: PropTypes.arrayOf(PropTypes.string).isRequired,
  onToggle: PropTypes.func.isRequired
};

export default TalearrLinkExistingFiles;
