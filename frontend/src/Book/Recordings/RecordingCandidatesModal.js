import PropTypes from 'prop-types';
import React from 'react';
import Alert from 'Components/Alert';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import TableRowCell from 'Components/Table/Cells/TableRowCell';
import Table from 'Components/Table/Table';
import TableBody from 'Components/Table/TableBody';
import TableRow from 'Components/Table/TableRow';
import { kinds, sizes } from 'Helpers/Props';
import { abridgedLabel, formatAbridgedSource, formatNarrators, formatRuntime, formatYear } from './recordingFormat';
import styles from './BookRecordings.css';

const columns = [
  { name: 'narrators', label: 'Narrator', isVisible: true },
  { name: 'abridged', label: 'Abridged', isVisible: true },
  { name: 'runtime', label: 'Runtime', isVisible: true },
  { name: 'edition', label: 'Edition', isVisible: true },
  { name: 'actions', label: '', isVisible: true }
];

function abridgedKind(abridged) {
  if (abridged === 'abridged') {
    return kinds.WARNING;
  }

  return abridged === 'unabridged' ? kinds.SUCCESS : kinds.DISABLED;
}

// TaleArr: pick the edition (or narrator) for "Add another recording". Lists the work's
// audiobook editions that are not a recording yet, near-identical ones folded together.
function RecordingCandidatesModal(props) {
  const {
    isOpen,
    title,
    message,
    candidates,
    addingKey,
    onAddPress,
    onModalClose
  } = props;

  return (
    <Modal
      isOpen={isOpen}
      size={sizes.EXTRA_LARGE}
      onModalClose={onModalClose}
    >
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {title}
        </ModalHeader>

        <ModalBody>
          {
            message &&
              <Alert kind={kinds.INFO}>
                {message}
              </Alert>
          }

          {
            candidates.length ?
              <Table columns={columns}>
                <TableBody>
                  {
                    candidates.map((candidate) => {
                      const key = candidate.editionId;
                      const year = formatYear(candidate.releaseDate);

                      return (
                        <TableRow key={key}>
                          <TableRowCell>
                            {formatNarrators(candidate.narrators)}
                          </TableRowCell>

                          <TableRowCell>
                            <Label
                              kind={abridgedKind(candidate.abridged)}
                              title={candidate.abridgedReason || ''}
                            >
                              {abridgedLabel(candidate.abridged)}
                            </Label>
                            <span className={styles.source}>
                              {candidate.abridgedSource === 'none' ? 'no source yet' : `from ${formatAbridgedSource(candidate.abridgedSource)}`}
                            </span>
                          </TableRowCell>

                          <TableRowCell>
                            {formatRuntime(candidate.runtimeMinutes)}
                          </TableRowCell>

                          <TableRowCell>
                            {[candidate.publisher, year, candidate.language, candidate.asin].filter(Boolean).join(', ') || '-'}
                            {
                              candidate.editionIds && candidate.editionIds.length > 1 &&
                                <span className={styles.source}>
                                  {`${candidate.editionIds.length} similar editions`}
                                </span>
                            }
                          </TableRowCell>

                          <TableRowCell className={styles.actions}>
                            <Button
                              size={sizes.SMALL}
                              kind={kinds.PRIMARY}
                              isDisabled={addingKey != null}
                              onPress={() => onAddPress(candidate)}
                            >
                              {addingKey === key ? 'Adding...' : 'Add'}
                            </Button>
                          </TableRowCell>
                        </TableRow>
                      );
                    })
                  }
                </TableBody>
              </Table> :
              <Alert kind={kinds.WARNING}>
                {'No other audiobook edition of this book is known.'}
              </Alert>
          }
        </ModalBody>

        <ModalFooter>
          <Button onPress={onModalClose}>
            {'Close'}
          </Button>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}

RecordingCandidatesModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  title: PropTypes.string.isRequired,
  message: PropTypes.string,
  candidates: PropTypes.arrayOf(PropTypes.object).isRequired,
  addingKey: PropTypes.number,
  onAddPress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default RecordingCandidatesModal;
