import PropTypes from 'prop-types';
import React, { Component } from 'react';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import Button from 'Components/Link/Button';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { inputTypes } from 'Helpers/Props';
import getErrorMessage from 'Utilities/Object/getErrorMessage';
import translate from 'Utilities/String/translate';

class EditBookModalContent extends Component {

  //
  // Listeners

  onSavePress = () => {
    const {
      onSavePress
    } = this.props;

    onSavePress(false);

  };

  //
  // Render

  render() {
    const {
      title,
      authorName,
      statistics,
      item,
      isFetching,
      isPopulated,
      error,
      isSaving,
      talearrTags,
      talearrSeriesOptions,
      talearrPreferredSeries,
      onInputChange,
      onTalearrTagsChange,
      onTalearrPreferredSeriesChange,
      onModalClose,
      ...otherProps
    } = this.props;

    const {
      monitored,
      anyEditionOk,
      editions
    } = item;

    const hasFile = statistics ? statistics.bookFileCount > 0 : false;

    // Talearr: a book in more than one series can pick the one used for naming and ordering.
    const preferredSeriesValues = [
      { key: '', value: 'As the metadata lists it' },
      ...talearrSeriesOptions.map((option) => ({
        key: option.title,
        value: option.position ? `${option.title} #${option.position}` : option.title
      }))
    ];
    const errorMessage = getErrorMessage(error, 'Unable to load editions');

    return (
      <ModalContent onModalClose={onModalClose}>
        <ModalHeader>
          {translate('EditBookHeader', { authorName, title })}
        </ModalHeader>

        <ModalBody>
          <Form
            {...otherProps}
          >
            <FormGroup>
              <FormLabel>
                {translate('Monitored')}
              </FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="monitored"
                helpText={translate('MonitoredHelpText')}
                {...monitored}
                onChange={onInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>
                {translate('AutomaticallySwitchEdition')}
              </FormLabel>

              <FormInputGroup
                type={inputTypes.CHECK}
                name="anyEditionOk"
                helpText={translate('AnyEditionOkHelpText')}
                {...anyEditionOk}
                onChange={onInputChange}
              />
            </FormGroup>

            <FormGroup>
              <FormLabel>
                {translate('Tags')}
              </FormLabel>

              <FormInputGroup
                type={inputTypes.TAG}
                name="talearrTags"
                helpText="Book tags. Use them in custom filters on the book index and the Wanted pages."
                value={talearrTags}
                onChange={onTalearrTagsChange}
              />
            </FormGroup>

            {
              talearrSeriesOptions.length > 1 &&
                <FormGroup>
                  <FormLabel>
                    {'Preferred series'}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.SELECT}
                    name="talearrPreferredSeries"
                    values={preferredSeriesValues}
                    value={talearrPreferredSeries}
                    helpText="The series used for the series naming tokens ({Book Series}, {Book SeriesPosition}, {Series Folder}) and for sorting by series. Kept through metadata refreshes."
                    onChange={onTalearrPreferredSeriesChange}
                  />
                </FormGroup>
            }

            {
              isFetching &&
                <LoadingIndicator />
            }

            {
              error &&
                <div>{errorMessage}</div>
            }

            {
              isPopulated && !isFetching && !!editions.value.length &&
                <FormGroup>
                  <FormLabel>
                    {translate('Edition')}
                  </FormLabel>

                  <FormInputGroup
                    type={inputTypes.BOOK_EDITION_SELECT}
                    name="editions"
                    helpText={translate('EditionsHelpText')}
                    isDisabled={anyEditionOk.value && hasFile}
                    bookEditions={editions}
                    onChange={onInputChange}
                  />
                </FormGroup>
            }

          </Form>
        </ModalBody>
        <ModalFooter>
          <Button
            onPress={onModalClose}
          >
            {translate('Cancel')}
          </Button>

          <SpinnerButton
            isSpinning={isSaving}
            onPress={this.onSavePress}
          >
            {translate('Save')}
          </SpinnerButton>
        </ModalFooter>

      </ModalContent>
    );
  }
}

EditBookModalContent.propTypes = {
  bookId: PropTypes.number.isRequired,
  title: PropTypes.string.isRequired,
  authorName: PropTypes.string.isRequired,
  statistics: PropTypes.object.isRequired,
  item: PropTypes.object.isRequired,
  isFetching: PropTypes.bool.isRequired,
  error: PropTypes.object,
  isPopulated: PropTypes.bool.isRequired,
  isSaving: PropTypes.bool.isRequired,
  talearrTags: PropTypes.arrayOf(PropTypes.number).isRequired,
  talearrSeriesOptions: PropTypes.arrayOf(PropTypes.object).isRequired,
  talearrPreferredSeries: PropTypes.string.isRequired,
  onInputChange: PropTypes.func.isRequired,
  onTalearrTagsChange: PropTypes.func.isRequired,
  onTalearrPreferredSeriesChange: PropTypes.func.isRequired,
  onSavePress: PropTypes.func.isRequired,
  onModalClose: PropTypes.func.isRequired
};

export default EditBookModalContent;
