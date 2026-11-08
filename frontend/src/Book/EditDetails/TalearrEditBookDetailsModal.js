import PropTypes from 'prop-types';
import React, { Component } from 'react';
import { connect } from 'react-redux';
import Alert from 'Components/Alert';
import Form from 'Components/Form/Form';
import FormGroup from 'Components/Form/FormGroup';
import FormInputGroup from 'Components/Form/FormInputGroup';
import FormLabel from 'Components/Form/FormLabel';
import TextInput from 'Components/Form/TextInput';
import Label from 'Components/Label';
import Button from 'Components/Link/Button';
import IconButton from 'Components/Link/IconButton';
import SpinnerButton from 'Components/Link/SpinnerButton';
import LoadingIndicator from 'Components/Loading/LoadingIndicator';
import Modal from 'Components/Modal/Modal';
import ModalBody from 'Components/Modal/ModalBody';
import ModalContent from 'Components/Modal/ModalContent';
import ModalFooter from 'Components/Modal/ModalFooter';
import ModalHeader from 'Components/Modal/ModalHeader';
import { icons, inputTypes, kinds, sizes } from 'Helpers/Props';
import { fetchBooks } from 'Store/Actions/bookActions';
import createAjaxRequest from 'Utilities/createAjaxRequest';
import styles from './TalearrEditBookDetailsModal.css';

const FIELDS = ['title', 'series', 'narrators'];

function readError(xhr, fallback) {
  const body = xhr && xhr.responseJSON;
  return (body && body.message) || fallback;
}

function toSeriesRows(entries) {
  return (entries || []).map((entry) => ({
    title: entry.title || '',
    position: entry.position || ''
  }));
}

function cleanSeries(rows) {
  return rows
    .map((row) => ({ title: row.title.trim(), position: row.position.trim() }))
    .filter((row) => row.title);
}

function sameSeries(rows, entries) {
  const left = cleanSeries(rows);
  const right = cleanSeries(toSeriesRows(entries));

  return left.length === right.length &&
    left.every((row, index) => {
      return row.title.toLowerCase() === right[index].title.toLowerCase() &&
        row.position === right[index].position;
    });
}

function sameNames(left, right) {
  const a = (left || []).map((name) => name.trim()).filter(Boolean);
  const b = (right || []).map((name) => name.trim()).filter(Boolean);

  return a.length === b.length && a.every((name, index) => name === b[index]);
}

// Talearr: "Edit book details". Fixes the title, series and narrators the metadata provider got
// wrong. The edits are kept through refreshes and can be put back to the provider's values; the
// API behind this modal is /api/v1/talearr/bookdetails.
class TalearrEditBookDetailsModal extends Component {

  //
  // Lifecycle

  constructor(props, context) {
    super(props, context);

    this._abortRequest = null;

    this.state = {
      isFetching: false,
      error: null,
      details: null,
      title: '',
      series: [],
      narrators: [],
      reset: {},
      isSaving: false,
      saveError: null
    };
  }

  componentDidMount() {
    if (this.props.isOpen) {
      this.fetchDetails();
    }
  }

  componentDidUpdate(prevProps) {
    if (this.props.isOpen && (!prevProps.isOpen || prevProps.bookId !== this.props.bookId)) {
      this.fetchDetails();
    }
  }

  componentWillUnmount() {
    if (this._abortRequest) {
      this._abortRequest();
      this._abortRequest = null;
    }
  }

  //
  // Data

  fetchDetails = () => {
    if (this._abortRequest) {
      this._abortRequest();
    }

    this.setState({ isFetching: true, error: null, saveError: null });

    const { request, abortRequest } = createAjaxRequest({
      url: `/talearr/bookdetails/${this.props.bookId}`
    });

    this._abortRequest = abortRequest;

    request.done((data) => {
      this.setDetails(data);
    });

    request.fail((xhr) => {
      if (xhr.aborted) {
        return;
      }

      this.setState({
        isFetching: false,
        details: null,
        error: readError(xhr, 'Unable to load the details of this book')
      });
    });
  };

  setDetails = (details) => {
    this.setState({
      isFetching: false,
      error: null,
      details,
      title: details.title.value || '',
      series: toSeriesRows(details.series.value),
      narrators: details.narrators ? (details.narrators.value || []) : [],
      reset: {}
    });
  };

  // What a field is compared with to decide whether it changed: the provider's value once the
  // user pressed "Use provider", else the value loaded from the server.
  baseline = (field) => {
    const { details, reset } = this.state;
    const value = details[field];

    return reset[field] ? value.providerValue : value.value;
  };

  //
  // Listeners

  onTitleChange = ({ value }) => {
    this.setState({ title: value });
  };

  onSeriesChange = (index, key, value) => {
    const series = this.state.series.map((row, rowIndex) => {
      return rowIndex === index ? { ...row, [key]: value } : row;
    });

    this.setState({ series });
  };

  onSeriesTitleChange = (index) => ({ value }) => {
    this.onSeriesChange(index, 'title', value);
  };

  onSeriesPositionChange = (index) => ({ value }) => {
    this.onSeriesChange(index, 'position', value);
  };

  onSeriesRemovePress = (index) => () => {
    this.setState({ series: this.state.series.filter((row, rowIndex) => rowIndex !== index) });
  };

  onAddSeriesPress = () => {
    this.setState({ series: [...this.state.series, { title: '', position: '' }] });
  };

  onPickSeries = ({ value }) => {
    const picked = (this.state.details.authorSeries || []).find((series) => series.id === value);

    if (!picked || this.state.series.some((row) => row.title.trim().toLowerCase() === picked.title.toLowerCase())) {
      return;
    }

    this.setState({ series: [...this.state.series, { title: picked.title, position: '' }] });
  };

  onNarratorsChange = ({ value }) => {
    this.setState({ narrators: value });
  };

  onUseProviderPress = (field) => () => {
    const { details } = this.state;
    const providerValue = details[field].providerValue;
    const reset = { ...this.state.reset, [field]: true };

    if (field === 'title') {
      this.setState({ reset, title: providerValue || '' });
    } else if (field === 'series') {
      this.setState({ reset, series: toSeriesRows(providerValue) });
    } else {
      this.setState({ reset, narrators: providerValue || [] });
    }
  };

  onSavePress = () => {
    const { details, title, series, narrators, reset } = this.state;
    const body = {
      reset: FIELDS.filter((field) => reset[field] && details[field] && details[field].edited)
    };

    if (title.trim() !== (this.baseline('title') || '')) {
      body.title = title.trim();
    }

    if (!sameSeries(series, this.baseline('series'))) {
      body.series = cleanSeries(series).map((row) => ({
        title: row.title,
        position: row.position || null
      }));
    }

    if (details.narrators && !sameNames(narrators, this.baseline('narrators'))) {
      body.narrators = narrators.map((name) => name.trim()).filter(Boolean);
    }

    if (!body.reset.length && body.title === undefined && body.series === undefined && body.narrators === undefined) {
      this.props.onModalClose();
      return;
    }

    this.setState({ isSaving: true, saveError: null });

    const { request } = createAjaxRequest({
      url: `/talearr/bookdetails/${this.props.bookId}`,
      method: 'PUT',
      dataType: 'json',
      data: JSON.stringify(body)
    });

    request.done((data) => {
      this.setState({ isSaving: false });
      this.setDetails(data);

      // The book and series routes return the edited details; reload the author's books.
      this.props.dispatchFetchBooks({ authorId: this.props.authorId });
      this.props.onModalClose();
    });

    request.fail((xhr) => {
      this.setState({
        isSaving: false,
        saveError: readError(xhr, 'Unable to save the details of this book')
      });
    });
  };

  //
  // Render

  renderFieldLabel(field, text) {
    const { details, reset } = this.state;
    const value = details[field];

    return (
      <FormLabel>
        {text}

        {
          value.edited && !reset[field] &&
            <Label
              className={styles.editedLabel}
              kind={kinds.INFO}
              size={sizes.SMALL}
            >
              {'Edited'}
            </Label>
        }
      </FormLabel>
    );
  }

  renderUseProvider(field) {
    const { details, reset } = this.state;

    if (!details[field].edited || reset[field]) {
      return null;
    }

    return (
      <Button
        className={styles.providerButton}
        size={sizes.SMALL}
        onPress={this.onUseProviderPress(field)}
      >
        {'Use provider value'}
      </Button>
    );
  }

  renderProviderValue(field, text) {
    const { details } = this.state;

    if (!details[field].edited) {
      return null;
    }

    return (
      <div className={styles.provider}>
        {`From the metadata provider: ${text || 'none'}`}
      </div>
    );
  }

  renderBody() {
    const {
      isFetching,
      error,
      details,
      title,
      series,
      narrators,
      saveError
    } = this.state;

    if (isFetching && !details) {
      return <LoadingIndicator />;
    }

    if (error) {
      return (
        <Alert kind={kinds.DANGER}>
          {error}
        </Alert>
      );
    }

    if (!details) {
      return null;
    }

    const providerSeries = (details.series.providerValue || [])
      .map((entry) => (entry.position ? `${entry.title} #${entry.position}` : entry.title))
      .join('; ');

    const seriesChoices = [
      { key: 0, value: 'Add one of this author\'s series', isDisabled: true },
      ...(details.authorSeries || []).map((option) => ({
        key: option.id,
        value: option.title,
        hint: option.createdByTalearr ? '(added in Talearr)' : undefined
      }))
    ];

    return (
      <Form>
        <p className={styles.intro}>
          {'Fix details the metadata provider got wrong. Your edits are kept when the book or author is refreshed, and apply to every format of this book.'}
        </p>

        <FormGroup>
          {this.renderFieldLabel('title', 'Title')}

          <div className={styles.field}>
            <FormInputGroup
              type={inputTypes.TEXT}
              name="title"
              value={title}
              onChange={this.onTitleChange}
            />

            {this.renderProviderValue('title', details.title.providerValue)}
            {this.renderUseProvider('title')}
          </div>
        </FormGroup>

        <FormGroup>
          {this.renderFieldLabel('series', 'Series')}

          <div className={styles.field}>
            {
              series.map((row, index) => {
                return (
                  <div
                    key={index}
                    className={styles.seriesRow}
                  >
                    <TextInput
                      className={styles.seriesTitle}
                      name={`seriesTitle${index}`}
                      value={row.title}
                      placeholder="Series name"
                      onChange={this.onSeriesTitleChange(index)}
                    />

                    <TextInput
                      className={styles.seriesPosition}
                      name={`seriesPosition${index}`}
                      value={row.position}
                      placeholder="#"
                      onChange={this.onSeriesPositionChange(index)}
                    />

                    <IconButton
                      name={icons.REMOVE}
                      title="Remove from this series"
                      onPress={this.onSeriesRemovePress(index)}
                    />
                  </div>
                );
              })
            }

            {
              !series.length &&
                <div className={styles.provider}>
                  {'Not in a series'}
                </div>
            }

            <div className={styles.seriesActions}>
              <Button
                size={sizes.SMALL}
                onPress={this.onAddSeriesPress}
              >
                {'Add series'}
              </Button>

              {
                !!(details.authorSeries || []).length &&
                  <FormInputGroup
                    className={styles.seriesPicker}
                    type={inputTypes.SELECT}
                    name="pickSeries"
                    value={0}
                    values={seriesChoices}
                    onChange={this.onPickSeries}
                  />
              }
            </div>

            <div className={styles.help}>
              {'A name that is not one of this author\'s series creates a new series. Leave # empty for an unnumbered book.'}
            </div>

            {this.renderProviderValue('series', providerSeries)}
            {this.renderUseProvider('series')}
          </div>
        </FormGroup>

        {
          details.narrators &&
            <FormGroup>
              {this.renderFieldLabel('narrators', 'Narrators')}

              <div className={styles.field}>
                <FormInputGroup
                  type={inputTypes.TEXT_TAG}
                  name="narrators"
                  value={narrators}
                  helpText="Narrators of the edition this audiobook follows. Press Enter after each name."
                  onChange={this.onNarratorsChange}
                />

                {this.renderProviderValue('narrators', (details.narrators.providerValue || []).join(', '))}
                {this.renderUseProvider('narrators')}
              </div>
            </FormGroup>
        }

        {
          saveError &&
            <Alert kind={kinds.DANGER}>
              {saveError}
            </Alert>
        }
      </Form>
    );
  }

  render() {
    const {
      isOpen,
      onModalClose
    } = this.props;

    const {
      details,
      isSaving
    } = this.state;

    return (
      <Modal
        isOpen={isOpen}
        size={sizes.MEDIUM}
        onModalClose={onModalClose}
      >
        <ModalContent onModalClose={onModalClose}>
          <ModalHeader>
            {'Edit book details'}
          </ModalHeader>

          <ModalBody>
            {this.renderBody()}
          </ModalBody>

          <ModalFooter>
            <Button onPress={onModalClose}>
              {'Cancel'}
            </Button>

            <SpinnerButton
              kind={kinds.PRIMARY}
              isSpinning={isSaving}
              isDisabled={!details}
              onPress={this.onSavePress}
            >
              {'Save'}
            </SpinnerButton>
          </ModalFooter>
        </ModalContent>
      </Modal>
    );
  }
}

TalearrEditBookDetailsModal.propTypes = {
  isOpen: PropTypes.bool.isRequired,
  bookId: PropTypes.number.isRequired,
  authorId: PropTypes.number.isRequired,
  onModalClose: PropTypes.func.isRequired,
  dispatchFetchBooks: PropTypes.func.isRequired
};

const mapDispatchToProps = {
  dispatchFetchBooks: fetchBooks
};

export default connect(null, mapDispatchToProps)(TalearrEditBookDetailsModal);
