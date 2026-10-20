import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Alert from 'Components/Alert';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes, kinds, sizes } from 'Helpers/Props';
import { describeRecording } from './recordingFormat';

// Talearr: manual "Remove this recording". Only an additional recording can be removed; the
// primary recording is the book itself, so the modal refuses it and points at Delete instead.
class RemoveRecordingModal extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this.state = {
      deleteFiles: false
    };
  }

  componentDidUpdate(prevProps) {
    if (this.props.isOpen && !prevProps.isOpen) {
      this.setState({ deleteFiles: false });
    }
  }

  //
  // Listeners

  onDeleteFilesChange = ({ value }) => {
    this.setState({ deleteFiles: value });
  };

  onRemovePress = () => {
    this.props.onRemovePress(this.state.deleteFiles);
  };

  //
  // Render

  render() {
    const {
      isOpen,
      recording,
      isRemoving,
      error,
      onModalClose
    } = this.props;

    const isPrimary = recording && recording.role === 'primary';
    const fileCount = recording && recording.files ? recording.files.count : 0;

    return (
      <Modal
        isOpen={isOpen}
        size={sizes.MEDIUM}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>
            {'Remove this recording'}
          </ModalHeader>

          <ModalBody>
            {
              recording &&
                <div>
                  <p>
                    <strong>{describeRecording(recording)}</strong>
                  </p>

                  {
                    isPrimary ?
                      <Alert kind={kinds.WARNING}>
                        {'This is the primary recording, the book itself. Talearr only removes additional recordings. To remove the primary recording, delete the book with Delete in the toolbar.'}
                      </Alert> :
                      <div>
                        <p>
                          {'The other recordings of this book are kept. Talearr never removes a recording on its own; this only happens when you confirm here.'}
                        </p>

                        <FormGroup>
                          <FormLabel>{'Delete files'}</FormLabel>

                          <FormInputGroup
                            type={inputTypes.CHECK}
                            name="deleteFiles"
                            value={this.state.deleteFiles}
                            helpText={fileCount ?
                              `Also delete the ${fileCount} file(s) of this recording from disk (to the recycle bin when one is set)` :
                              'This recording has no files'}
                            kind={kinds.DANGER}
                            isDisabled={!fileCount}
                            onChange={this.onDeleteFilesChange}
                          />
                        </FormGroup>
                      </div>
                  }

                  {
                    error &&
                      <Alert kind={kinds.DANGER}>
                        {error}
                      </Alert>
                  }
                </div>
            }
          </ModalBody>

          <ModalFooter>
            <Button onPress={onModalClose}>
              {'Close'}
            </Button>

            {
              !isPrimary &&
                <SpinnerButton
                  kind={kinds.DANGER}
                  isSpinning={isRemoving}
                  onPress={this.onRemovePress}
                >
                  {'Remove recording'}
                </SpinnerButton>
            }
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

RemoveRecordingModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  recording: PropTypes.object,
  isRemoving: PropTypes.bool.isRequired,
  error: PropTypes.string,
  onRemovePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default RemoveRecordingModal;
