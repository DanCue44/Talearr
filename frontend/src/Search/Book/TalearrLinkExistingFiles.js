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
// A folder whose files Talearr already tracks on another audiobook (or as unmapped files) is
// listed too, never preselected; ticking it moves those files to the new book (database only).
function TalearrLinkExistingFiles(props) {
  const {
    candidates,
    selectedPaths,
    onToggle
  } = props;

  if (!candidates || !candidates.length) {
    return null;
  }

  const hasTracked = candidates.some((candidate) => candidate.trackedElsewhere);

  return (
    <div className={styles.container}>
      <div className={styles.heading}>
        {'Link Existing Files'}
      </div>

      <div className={styles.help}>
        {'Import these files to the new audiobook instead of searching for it. Works like Manual Import: files outside your root folders are hardlinked or copied, the originals stay where they are.'}
      </div>

      {
        hasTracked ?
          <div className={styles.help}>
            {'Ticking files that are already in Talearr moves them to the new book. Nothing on disk is moved or renamed.'}
          </div> :
          null
      }

      {
        candidates.map((candidate) => {
          const duration = formatLinkDuration(candidate.durationSeconds);
          const isSelected = selectedPaths.includes(candidate.path);
          let matchLabel = candidate.strong ? 'Strong match' : 'Possible match';

          if (candidate.trackedElsewhere) {
            matchLabel = null;
          }

          const meta = [
            describeSource(candidate),
            duration,
            `${candidate.fileCount} ${candidate.fileCount === 1 ? 'file' : 'files'}`,
            candidate.narrators ? `Narrated by ${candidate.narrators}` : null,
            matchLabel
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

                {
                  candidate.trackedElsewhere && candidate.trackedOnLabel ?
                    <div className={styles.trackedOn}>
                      {candidate.trackedOnLabel}
                    </div> :
                    null
                }
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
